using System.Globalization;

namespace Thermal.Telemetry;

public record MetricSummary(string Key, string Label, string Unit, double? Min, double? Avg, double? Max);
public record RecordingInfo(bool Active, long StartedAt, double Seconds, string? Path, MetricSummary[] Summary);

/// <summary>Writes one CSV row per second while a session is recording, and keeps min/avg/max per metric.</summary>
internal sealed class Recorder
{
    private sealed class Stat
    {
        public string Label = "", Unit = "";
        public double Min = double.MaxValue, Max = double.MinValue, Sum;
        public int N;
        public void Add(double v) { Min = Math.Min(Min, v); Max = Math.Max(Max, v); Sum += v; N++; }
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Thermal sessions");

    private StreamWriter? _csv;
    private string[]? _keys;
    private readonly Dictionary<string, Stat> _stats = new();
    private DateTimeOffset _started;
    private string? _path;
    private RecordingInfo _last = new(false, 0, 0, null, Array.Empty<MetricSummary>());

    public bool Active => _csv != null;

    public void Start()
    {
        if (Active) return;
        Directory.CreateDirectory(Folder);
        _started = DateTimeOffset.Now;
        _path = Path.Combine(Folder, $"session-{_started:yyyy-MM-dd_HH-mm-ss}.csv");
        _csv = new StreamWriter(_path) { AutoFlush = true };
        _keys = null;
        _stats.Clear();
    }

    public void Stop()
    {
        if (!Active) return;
        _last = Info();
        _last = _last with { Active = false };
        _csv!.Dispose();
        _csv = null;
    }

    public void Add(Snapshot s, FpsInfo? fps)
    {
        if (_csv == null) return;
        var row = Metrics(s, fps);
        if (_keys == null)
        {
            _keys = row.Select(r => r.key).ToArray();
            _csv.WriteLine("time," + string.Join(",", row.Select(r => Csv($"{r.label} ({r.unit})"))));
        }
        var values = new List<string>();
        foreach (var k in _keys)
        {
            var m = row.FirstOrDefault(r => r.key == k);
            if (m.key != null && m.value is double v)
            {
                values.Add(v.ToString("0.##", CultureInfo.InvariantCulture));
                if (!_stats.TryGetValue(k, out var st)) _stats[k] = st = new Stat { Label = m.label, Unit = m.unit };
                st.Add(v);
            }
            else values.Add("");
        }
        _csv.WriteLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "," + string.Join(",", values));
    }

    public RecordingInfo Info()
    {
        if (!Active) return _last;
        var summary = (_keys ?? Array.Empty<string>())
            .Where(_stats.ContainsKey)
            .Select(k => { var s = _stats[k]; return new MetricSummary(k, s.Label, s.Unit, Math.Round(s.Min, 1), Math.Round(s.Sum / s.N, 1), Math.Round(s.Max, 1)); })
            .ToArray();
        return new RecordingInfo(true, _started.ToUnixTimeMilliseconds(), Math.Round((DateTimeOffset.Now - _started).TotalSeconds), _path, summary);
    }

    private static List<(string key, string label, string unit, double? value)> Metrics(Snapshot s, FpsInfo? fps)
    {
        var m = new List<(string, string, string, double?)>
        {
            ("fps", "Frame rate", "fps", fps?.Fps),
            ("fps.low", "1% low", "fps", fps?.Low1),
            ("cpu.temp", "CPU temperature", "°C", s.Cpu.Temp),
            ("cpu.load", "CPU load", "%", s.Cpu.Load),
            ("cpu.clock", "CPU clock", "MHz", s.Cpu.ClockAvg),
            ("cpu.power", "CPU power", "W", s.Cpu.Power),
        };
        foreach (var g in s.Gpus)
        {
            var n = g.Kind == "discrete" ? "dGPU" : "iGPU";
            m.Add(($"{g.Id}.temp", $"{n} temperature", "°C", g.Temp));
            m.Add(($"{g.Id}.load", $"{n} load", "%", g.Load));
            m.Add(($"{g.Id}.clock", $"{n} clock", "MHz", g.ClockCore));
            m.Add(($"{g.Id}.power", $"{n} power", "W", g.Power));
            m.Add(($"{g.Id}.mem", $"{n} memory used", "MB", g.MemUsed / 1048576.0));
        }
        for (var i = 0; i < s.Fans.Length; i++) m.Add(($"fan{i}", s.Fans[i].Name, "rpm", s.Fans[i].Rpm));
        m.Add(("ram", "Memory in use", "GB", s.Memory.Used / 1073741824.0));
        m.Add(("battery", "Battery power", "W", s.Battery?.RateW));
        return m;
    }

    private static string Csv(string s) => s.Contains(',') ? $"\"{s}\"" : s;
}
