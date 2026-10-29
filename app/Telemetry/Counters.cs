using System.Diagnostics;

namespace Thermal.Telemetry;

/// <summary>
/// Reads whole perf-counter categories in one call per tick. Rate counters ("% Processor Time",
/// "Utilization Percentage", "IO Data Bytes/sec") need two raw samples, so the previous read is kept.
/// </summary>
internal sealed class CategoryReader
{
    private readonly PerformanceCounterCategory _cat;
    private InstanceDataCollectionCollection? _prev;

    public CategoryReader(string name) => _cat = new PerformanceCounterCategory(name);

    public (InstanceDataCollectionCollection now, InstanceDataCollectionCollection? prev) Read()
    {
        var now = _cat.ReadCategory();
        var prev = _prev;
        _prev = now;
        return (now, prev);
    }

    public static float Rate(InstanceDataCollectionCollection now, InstanceDataCollectionCollection? prev, string counter, string instance)
    {
        if (prev == null) return 0;
        var a = prev[counter]?[instance];
        var b = now[counter]?[instance];
        if (a == null || b == null) return 0;
        return CounterSample.Calculate(a.Sample, b.Sample);
    }

    public static long Raw(InstanceDataCollectionCollection now, string counter, string instance) =>
        now[counter]?[instance]?.RawValue ?? 0;
}

internal sealed record ProcRow(int Pid, string Name, double Cpu, long Ram, long Commit, long Io, int Threads)
{
    public double Gpu { get; set; }
    public string GpuLuid { get; set; } = "";
    public string GpuEngine { get; set; } = "";
    public long Vram { get; set; }
}

internal sealed class ProcessSampler
{
    private readonly CategoryReader _proc = new("Process V2");
    private readonly CategoryReader _engine = new("GPU Engine");
    private readonly CategoryReader _gpuMem = new("GPU Process Memory");
    private readonly CategoryReader _adapterMem = new("GPU Adapter Memory");
    private readonly int _cpus = Environment.ProcessorCount;

    /// <summary>Per adapter LUID: utilisation per engine type, the way Task Manager computes it.</summary>
    public Dictionary<string, Dictionary<string, double>> AdapterEngines { get; } = new();
    public Dictionary<string, (long dedicated, long shared)> AdapterMemory { get; } = new();

    public List<ProcRow> Sample()
    {
        var rows = new Dictionary<int, ProcRow>();
        var (now, prev) = _proc.Read();
        var ids = now["Process ID"];
        if (ids != null)
        {
            foreach (InstanceData inst in ids.Values)
            {
                var key = inst.InstanceName;
                if (key == "_Total" || key.StartsWith("Idle:")) continue;
                var pid = (int)inst.RawValue;
                if (pid == 0) continue;
                var name = key[..Math.Max(0, key.LastIndexOf(':'))];
                rows[pid] = new ProcRow(
                    pid,
                    name,
                    CategoryReader.Rate(now, prev, "% Processor Time", key) / _cpus,
                    CategoryReader.Raw(now, "Working Set - Private", key),
                    CategoryReader.Raw(now, "Private Bytes", key),
                    (long)CategoryReader.Rate(now, prev, "IO Data Bytes/sec", key),
                    (int)CategoryReader.Raw(now, "Thread Count", key));
            }
        }

        SampleGpu(rows);
        return rows.Values.ToList();
    }

    private void SampleGpu(Dictionary<int, ProcRow> rows)
    {
        // pid_1234_luid_0x00000000_0x0000D1C3_phys_0_eng_0_engtype_3D
        var (now, prev) = _engine.Read();
        var perProc = new Dictionary<(int pid, string luid, string eng), double>();
        AdapterEngines.Clear();
        var util = now["Utilization Percentage"];
        if (util != null && prev != null)
        {
            foreach (InstanceData inst in util.Values)
            {
                var n = inst.InstanceName;
                var v = CategoryReader.Rate(now, prev, "Utilization Percentage", n);
                if (v <= 0) continue;
                var p = Parse(n);
                if (p == null) continue;
                var (pid, luid, eng) = p.Value;
                perProc[(pid, luid, eng)] = perProc.GetValueOrDefault((pid, luid, eng)) + v;
                if (!AdapterEngines.TryGetValue(luid, out var engines)) AdapterEngines[luid] = engines = new();
                engines[eng] = engines.GetValueOrDefault(eng) + v;
            }
        }
        foreach (var ((pid, luid, eng), v) in perProc)
        {
            if (!rows.TryGetValue(pid, out var row) || v <= row.Gpu) continue;
            row.Gpu = Math.Min(100, v);
            row.GpuLuid = luid;
            row.GpuEngine = eng;
        }
        foreach (var engines in AdapterEngines.Values)
            foreach (var k in engines.Keys.ToList()) engines[k] = Math.Min(100, engines[k]);

        var (mem, _) = _gpuMem.Read();
        var ded = mem["Dedicated Usage"];
        if (ded != null)
        {
            foreach (InstanceData inst in ded.Values)
            {
                var n = inst.InstanceName; // pid_1234_luid_..._phys_0
                if (!n.StartsWith("pid_")) continue;
                var end = n.IndexOf('_', 4);
                if (end < 0 || !int.TryParse(n.AsSpan(4, end - 4), out var pid)) continue;
                if (rows.TryGetValue(pid, out var row)) row.Vram += inst.RawValue;
            }
        }

        AdapterMemory.Clear();
        var (am, _) = _adapterMem.Read();
        var aDed = am["Dedicated Usage"];
        if (aDed != null)
        {
            foreach (InstanceData inst in aDed.Values)
            {
                var n = inst.InstanceName;
                var luid = n.Length >= 26 ? n[..26] : n;
                var shared = CategoryReader.Raw(am, "Shared Usage", n);
                var cur = AdapterMemory.GetValueOrDefault(luid);
                AdapterMemory[luid] = (cur.dedicated + inst.RawValue, cur.shared + shared);
            }
        }
    }

    private static (int pid, string luid, string eng)? Parse(string n)
    {
        if (!n.StartsWith("pid_")) return null;
        var pidEnd = n.IndexOf('_', 4);
        var luidStart = n.IndexOf("luid_", StringComparison.Ordinal);
        var engIdx = n.IndexOf("_engtype_", StringComparison.Ordinal);
        if (pidEnd < 0 || luidStart < 0 || engIdx < 0) return null;
        if (!int.TryParse(n.AsSpan(4, pidEnd - 4), out var pid)) return null;
        var luid = n.Substring(luidStart, 26); // luid_0xHHHHHHHH_0xLLLLLLLL
        return (pid, luid, n[(engIdx + 9)..]);
    }
}
