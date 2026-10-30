namespace Thermal.Telemetry;

/// <summary>
/// Raises a notification when a temperature stays at or above its limit for the configured time, then stays quiet
/// until it has dropped 5 °C below the limit.
/// </summary>
internal sealed class Alerts
{
    private sealed class Rule
    {
        public DateTime? Since;
        public bool Fired;
    }

    private readonly Dictionary<string, Rule> _rules = new();

    public IEnumerable<string> Evaluate(Snapshot s, Settings settings)
    {
        if (!settings.AlertsEnabled) yield break;
        var heaviest = s.Processes.OrderByDescending(p => p.Cpu + p.Gpu).FirstOrDefault();
        var culprit = heaviest == null ? "" : $" Busiest app: {(heaviest.Title != "" ? heaviest.Title : heaviest.Name)}.";

        var cpu = Check("cpu", s.Cpu.Temp, settings.CpuTempAlert, settings.AlertSeconds);
        if (cpu) yield return $"CPU has been at {s.Cpu.Temp:0} °C for {settings.AlertSeconds} seconds.{culprit}";

        foreach (var g in s.Gpus)
        {
            if (Check(g.Id, g.Temp, settings.GpuTempAlert, settings.AlertSeconds))
                yield return $"{g.Name} has been at {g.Temp:0} °C for {settings.AlertSeconds} seconds.{culprit}";
        }
    }

    private bool Check(string key, double? temp, double limit, int seconds)
    {
        if (!_rules.TryGetValue(key, out var r)) _rules[key] = r = new Rule();
        if (temp == null) { r.Since = null; return false; }
        if (temp >= limit)
        {
            r.Since ??= DateTime.UtcNow;
            if (!r.Fired && DateTime.UtcNow - r.Since >= TimeSpan.FromSeconds(seconds))
            {
                r.Fired = true;
                return true;
            }
        }
        else
        {
            r.Since = null;
            if (temp < limit - 5) r.Fired = false;
        }
        return false;
    }
}
