using System.Management;

namespace Thermal.Telemetry;

public record AppPower(string Name, string Title, double Share, double? Watts, double? CpuW, double? GpuW, double? Wh);
/// <summary>An app holding the dedicated GPU awake. Pref: 0 Windows decides, 1 power saving, 2 high performance; null = never set.</summary>
public record WakeApp(string Name, string Title, string? Path, int? Pref, bool Packaged);
public record GpuPower(string Id, string Name, string Kind, double? Watts, bool Awake, WakeApp[] WakeApps, bool OnlyWindows, bool WokenByThermal);
public record BatteryHealth(double? DesignWh, double? FullWh, double? WearPct, int? Cycles);
public record PowerInfo(
    bool OnBattery, double? DrainW, double? CpuW, GpuPower[] Gpus, double? RestW, AppPower[] Apps,
    double SinceMinutes, double? SinceWh, AppPower[] SinceApps, BatteryHealth? Health, UnplugInfo? Unplugged);

/// <summary>
/// Windows has no live per-app power meter, so this splits the measured CPU package power and each GPU's power
/// between processes by their share of CPU time and GPU engine time. What the battery drains beyond that goes to
/// the screen, Wi-Fi, storage and the rest of the board. Energy per app is summed while running on battery.
/// </summary>
internal sealed class PowerModel
{
    private readonly Dictionary<string, (string title, double wh)> _since = new();
    private double _sinceWh, _sinceSeconds;
    private bool _wasOnBattery;
    private DateTime _last = DateTime.UtcNow;
    private BatteryHealth? _health;
    private DateTime _healthRead = DateTime.MinValue;
    private readonly int _self = Environment.ProcessId;
    private readonly BatteryHistory _history = new();

    public PowerInfo Build(Snapshot s, List<ProcRow> procs, IReadOnlyDictionary<string, string> luidOfGpuId, bool pollingDgpu, Func<int, string?> pathOf)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(5, (now - _last).TotalSeconds);
        _last = now;

        var onBattery = s.Battery is { OnAc: false };
        double? drain = onBattery && s.Battery!.RateW is < 0 ? -s.Battery.RateW : null;
        var cpuW = s.Cpu.Power;

        // CPU: share of all processes' CPU time.
        var cpuSum = procs.Sum(p => p.Cpu);

        // GPU: share of each adapter's busiest-engine time.
        var gpuRows = new List<GpuPower>();
        var gpuShares = new Dictionary<int, double>(); // pid -> watts from GPUs
        foreach (var g in s.Gpus)
        {
            var luid = luidOfGpuId.GetValueOrDefault(g.Id, "");
            var users = procs.Where(p => p.GpuByLuid.GetValueOrDefault(luid) > 0).ToList();
            var sum = users.Sum(p => p.GpuByLuid[luid]);
            if (g.Power is double gw && sum > 0)
                foreach (var p in users) gpuShares[p.Pid] = gpuShares.GetValueOrDefault(p.Pid) + gw * p.GpuByLuid[luid] / sum;

            var wake = Array.Empty<WakeApp>();
            var onlyWindows = false;
            var awake = true;
            if (g.Kind == "discrete")
            {
                // On hybrid-graphics laptops the dedicated GPU powers off unless something holds memory or work on it.
                var holders = procs.Where(p => p.Pid != _self && p.Name != "Thermal"
                                               && (p.VramByLuid.GetValueOrDefault(luid) > 0 || p.GpuByLuid.GetValueOrDefault(luid) > 0))
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(grp => (name: grp.Key, path: grp.Select(p => pathOf(p.Pid)).FirstOrDefault(x => x != null), pid: grp.First().Pid))
                    .ToList();
                var userApps = holders.Where(h => !GpuPreference.IsSystem(h.name, h.path)).ToList();
                onlyWindows = userApps.Count == 0 && holders.Count > 0;
                wake = userApps
                    .Select(h => new WakeApp(h.name, s.Processes.FirstOrDefault(x => x.Pid == h.pid)?.Title ?? "", h.path,
                        h.path == null ? null : GpuPreference.Get(h.path), h.path != null && GpuPreference.IsPackaged(h.path)))
                    .OrderBy(w => w.Title == "" ? w.Name : w.Title, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                awake = holders.Count > 0 || pollingDgpu || (g.Power ?? 0) > 1;
            }
            gpuRows.Add(new GpuPower(g.Id, g.Name, g.Kind, g.Power, awake, wake, onlyWindows, g.Kind == "discrete" && pollingDgpu && wake.Length == 0 && !onlyWindows));
        }

        var gpuW = s.Gpus.Sum(g => g.Power ?? 0);
        double? rest = drain != null && cpuW != null ? Math.Max(0, drain.Value - cpuW.Value - gpuW) : null;

        // Per app, grouped by executable name.
        var apps = procs
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(grp =>
            {
                var title = s.Processes.FirstOrDefault(x => x.Pid == grp.First().Pid)?.Title ?? "";
                var cpuShare = cpuSum > 0 ? grp.Sum(p => p.Cpu) / cpuSum : 0;
                double? c = cpuW * cpuShare;
                var gw = grp.Sum(p => gpuShares.GetValueOrDefault(p.Pid));
                double? total = cpuW == null && gw == 0 ? null : (c ?? 0) + gw;
                var share = (cpuShare * (cpuW ?? 1) + gw) / Math.Max(1e-6, (cpuW ?? 1) + gpuW);
                return new AppPower(grp.Key, title, share, Round(total), Round(c), Round(gw), null);
            })
            .Where(a => a.Share > 0.001)
            .OrderByDescending(a => a.Watts ?? a.Share)
            .ToList();

        // Energy since unplugged.
        if (onBattery && !_wasOnBattery)
        {
            _since.Clear();
            _sinceWh = 0;
            _sinceSeconds = 0;
        }
        _wasOnBattery = onBattery;
        if (onBattery)
        {
            _sinceSeconds += dt;
            if (drain != null) _sinceWh += drain.Value * dt / 3600;
            foreach (var a in apps.Where(a => a.Watts != null))
            {
                var cur = _since.GetValueOrDefault(a.Name);
                _since[a.Name] = (a.Title, cur.wh + a.Watts!.Value * dt / 3600);
            }
        }
        var sinceApps = _since.OrderByDescending(kv => kv.Value.wh).Take(10)
            .Select(kv => new AppPower(kv.Key, kv.Value.title, _sinceWh > 0 ? kv.Value.wh / _sinceWh : 0, null, null, null, Math.Round(kv.Value.wh, 2)))
            .ToArray();

        if (now - _healthRead > TimeSpan.FromMinutes(5)) { _health = ReadHealth(); _healthRead = now; }

        return new PowerInfo(onBattery, Round(drain), Round(cpuW), gpuRows.ToArray(), Round(rest), apps.Take(12).ToArray(),
            Math.Round(_sinceSeconds / 60, 1), _sinceSeconds > 0 ? Math.Round(_sinceWh, 2) : null, sinceApps, _health, _history.Get(onBattery));
    }

    private static double? Round(double? v) => v == null ? null : Math.Round(v.Value, 2);

    private static BatteryHealth? ReadHealth()
    {
        static double? Read(string cls, string prop)
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\wmi", $"SELECT {prop} FROM {cls}");
                var o = s.Get().Cast<ManagementBaseObject>().FirstOrDefault();
                return o == null ? null : Convert.ToDouble(o[prop]);
            }
            catch { return null; }
        }
        var design = Read("BatteryStaticData", "DesignedCapacity");
        var full = Read("BatteryFullChargedCapacity", "FullChargedCapacity");
        var cycles = Read("BatteryCycleCount", "CycleCount");
        if (design == null && full == null) return null;
        double? wear = design is > 0 && full is > 0 ? Math.Round(Math.Max(0, 100 * (1 - full.Value / design.Value)), 1) : null;
        return new BatteryHealth(design / 1000, full / 1000, wear, cycles is > 0 ? (int)cycles : null);
    }
}
