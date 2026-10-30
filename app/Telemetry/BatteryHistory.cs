using System.Diagnostics;
using System.Globalization;
using System.Xml;

namespace Thermal.Telemetry;

/// <summary>When the charger came out and what the battery has done since, from Windows' own records.</summary>
public record UnplugInfo(long At, double ChargeAtWh, double UsedWh, double AwakeMinutes, double AwakeWh, double AsleepMinutes, double AsleepWh);

/// <summary>
/// Reads the "Recent usage" table of `powercfg /batteryreport /xml`. Windows logs every power-state change there
/// (on AC or battery, active, standby, hibernate) with the battery's charge, even while Thermal isn't running.
/// Generating the report takes a second or two, so it runs in the background every two minutes.
/// </summary>
internal sealed class BatteryHistory
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "thermal-battery-report.xml");
    private UnplugInfo? _last;
    private DateTime _read = DateTime.MinValue;
    private int _running;

    public UnplugInfo? Get(bool onBattery)
    {
        if (!onBattery) { _last = null; _read = DateTime.MinValue; return null; }
        if (DateTime.UtcNow - _read > TimeSpan.FromMinutes(2) && Interlocked.Exchange(ref _running, 1) == 0)
        {
            _read = DateTime.UtcNow;
            Task.Run(() =>
            {
                try { _last = Read(); }
                catch (Exception e) { MainForm.Log(e); }
                finally { Interlocked.Exchange(ref _running, 0); }
            });
        }
        return _last;
    }

    private UnplugInfo? Read()
    {
        using (var p = Process.Start(new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /duration 7 /output \"{_file}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            if (!p.WaitForExit(15000)) { p.Kill(); return null; }
        }
        var doc = new XmlDocument();
        doc.Load(_file);
        var entries = doc.SelectNodes("//*[local-name()='RecentUsage']/*[local-name()='UsageEntry']")!
            .Cast<XmlElement>()
            .Select(e => (
                at: DateTime.Parse(e.GetAttribute("Timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                ac: e.GetAttribute("Ac") == "1",
                type: e.GetAttribute("EntryType"),
                charge: double.TryParse(e.GetAttribute("ChargeCapacity"), CultureInfo.InvariantCulture, out var c) ? c : 0))
            .ToList();
        if (entries.Count == 0) return null;

        // The current battery stretch starts at the first entry after the last one on AC power.
        var lastAc = entries.FindLastIndex(e => e.ac && e.type != "ReportGenerated");
        var start = lastAc + 1;
        if (start >= entries.Count || lastAc < 0) return null; // unplugged longer than the report covers
        var chargeAt = entries[start].charge;

        double awakeMin = 0, awakeWh = 0, asleepMin = 0, asleepWh = 0;
        for (var i = start; i < entries.Count - 1; i++)
        {
            var e = entries[i];
            var next = entries[i + 1];
            var minutes = (next.at - e.at).TotalMinutes;
            var used = Math.Max(0, e.charge - next.charge) / 1000;
            if (e.type == "Active") { awakeMin += minutes; awakeWh += used; }
            else { asleepMin += minutes; asleepWh += used; }
        }
        var now = entries[^1];
        return new UnplugInfo(
            new DateTimeOffset(entries[start].at, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Math.Round(chargeAt / 1000, 1),
            Math.Round(Math.Max(0, chargeAt - now.charge) / 1000, 1),
            Math.Round(awakeMin), Math.Round(awakeWh, 1), Math.Round(asleepMin), Math.Round(asleepWh, 1));
    }
}
