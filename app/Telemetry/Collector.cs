using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace Thermal.Telemetry;

/// <summary>Builds one <see cref="Snapshot"/> per tick from LibreHardwareMonitor, the HP BIOS, perf counters and DXGI.</summary>
internal sealed class Collector : IDisposable
{
    private Computer _computer;
    private bool _gpuOpen;
    private readonly DevicePower? _dgpuPower;
    private string _dgpuLuid = "";
    private DateTime? _dgpuUsedSince, _dgpuIdleSince;
    private bool? _dgpuOn;
    private readonly int _self = Environment.ProcessId;
    private readonly HpBios _hp;
    private readonly ProcessSampler _procs = new();
    private readonly PowerModel _power = new();
    private readonly Dictionary<string, string> _gpuLuids = new();
    private readonly Dictionary<int, string> _descriptions = new();
    private readonly PerformanceCounter? _cpuPerf;
    private readonly double _baseClock;
    private readonly bool _elevated;
    private readonly string _machineName, _model, _os;
    private List<Dxgi.Adapter> _adapters;
    private DateTime _adaptersRefreshed = DateTime.UtcNow;

    /// <summary>
    /// Whether to read the dedicated GPU's sensors while an app is using it. LibreHardwareMonitor's connection to the
    /// NVIDIA driver keeps a hybrid-graphics GPU powered, so Thermal only connects while something else already woke it.
    /// </summary>
    public bool PollDiscreteGpu { get; set; } = true;

    public Collector()
    {
        _elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        _hp = new HpBios();
        _adapters = Dxgi.Enumerate();
        var discrete = _adapters.FirstOrDefault(a => a.DedicatedVideo >= 1UL << 30);
        if (discrete != null && _adapters.Count > 1)
        {
            _dgpuLuid = discrete.Luid;
            var power = new DevicePower((ushort)discrete.VendorId);
            _dgpuPower = power.Found ? power : null;
        }
        // Start disconnected from the GPUs if the dedicated one is asleep, so opening Thermal doesn't wake it.
        _gpuOpen = _dgpuPower?.IsOn() != false;
        _computer = OpenComputer(_gpuOpen);

        try { _cpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total"); _cpuPerf.NextValue(); }
        catch { _cpuPerf = null; }

        _machineName = Environment.MachineName;
        _model = Wmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem", o => $"{o["Model"]}") ?? "";
        _baseClock = Wmi("SELECT MaxClockSpeed FROM Win32_Processor", o => Convert.ToDouble(o["MaxClockSpeed"])) ;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber") as string ?? "";
        // ProductName still says "Windows 10" on Windows 11; the build number is the tell.
        var product = (key?.GetValue("ProductName") as string ?? "Windows").Replace("Windows 10", int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10");
        _os = $"{product} {key?.GetValue("DisplayVersion")} ({build})";
    }

    private static Computer OpenComputer(bool gpus)
    {
        var c = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = gpus,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
        };
        c.Open();
        return c;
    }

    public Snapshot Collect()
    {
        var notes = new List<Note>();
        var procs = _procs.Sample();
        RefreshAdaptersIfNeeded(procs);

        var wantGpu = DecideGpuConnection(procs);
        if (_reopen || wantGpu != _gpuOpen)
        {
            _reopen = false;
            _gpuOpen = wantGpu;
            _computer.Close();
            _computer = OpenComputer(_gpuOpen);
        }
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.GpuNvidia && !PollDiscreteGpu) continue;
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }

        var cpu = BuildCpu(notes);
        var gpus = BuildGpus();
        var fans = BuildFans();
        if (!_elevated) notes.Add(new Note("admin", "Running without administrator rights. Fan speeds, CPU temperature and power need Thermal to run as administrator."));

        var adapterNames = _adapters.ToDictionary(a => a.Luid, a => a.Name);
        var procInfos = procs
            .Select(p => new ProcInfo(p.Pid, p.Name, Describe(p.Pid), Math.Round(p.Cpu, 2), p.Ram, p.Commit, Math.Round(p.Gpu, 2),
                adapterNames.GetValueOrDefault(p.GpuLuid, p.GpuLuid == "" ? "" : "GPU"), p.GpuEngine, p.Vram, p.Io, p.Threads))
            .ToArray();
        PruneDescriptions(procs);

        var snap = new Snapshot(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new MachineInfo(_machineName, _model, _os, Environment.TickCount64 / 1000, _elevated),
            cpu, gpus, fans, BuildMemory(), BuildBattery(), BuildDisks(), BuildNet(), procInfos, BuildSensors(), notes.ToArray());
        return snap with { Power = _power.Build(snap, procs, _gpuLuids, _dgpuOn, _gpuOpen && UsersOfDgpu(procs).Count == 0, PathOf) };
    }

    /// <summary>Apps (not Windows itself, not Thermal) holding real work or memory on the dedicated GPU.</summary>
    private List<ProcRow> UsersOfDgpu(List<ProcRow> procs) =>
        _dgpuLuid == "" ? new List<ProcRow>() : procs.Where(p =>
            p.Pid != _self && p.Name != "Thermal" && !GpuPreference.IsSystem(p.Name, null)
            && (p.VramByLuid.GetValueOrDefault(_dgpuLuid) > 1 << 20 || p.GpuByLuid.GetValueOrDefault(_dgpuLuid) > 0.5)).ToList();

    /// <summary>
    /// Connect to GPU sensors only while an app keeps the dedicated GPU awake anyway: after 2 s of use, and until it
    /// has been unused for 20 s. Machines without a sleeping dedicated GPU stay connected.
    /// </summary>
    private bool DecideGpuConnection(List<ProcRow> procs)
    {
        if (_dgpuPower == null) { _dgpuOn = null; return true; }
        _dgpuOn = _dgpuPower.IsOn();
        var used = UsersOfDgpu(procs).Count > 0;
        var now = DateTime.UtcNow;
        if (used) { _dgpuUsedSince ??= now; _dgpuIdleSince = null; }
        else { _dgpuIdleSince ??= now; _dgpuUsedSince = null; }

        if (!PollDiscreteGpu) return false;
        if (_gpuOpen) return !(_dgpuIdleSince != null && now - _dgpuIdleSince > TimeSpan.FromSeconds(20));
        return _dgpuOn == true && _dgpuUsedSince != null && now - _dgpuUsedSince > TimeSpan.FromSeconds(2);
    }

    /// <summary>Whether Thermal is connected to the GPU sensors right now (shown in the UI).</summary>
    public bool GpuSensorsConnected => _gpuOpen;

    // ---------- CPU ----------

    private CpuInfo BuildCpu(List<Note> notes)
    {
        var hw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var name = hw?.Name ?? "CPU";
        var sensors = hw?.Sensors ?? Array.Empty<ISensor>();

        var total = Val(sensors, SensorType.Load, "CPU Total") ?? 0;
        var threads = sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #") && s.Name.Contains("Thread"))
            .Select(s => (double)(s.Value ?? 0)).ToArray();
        if (threads.Length == 0)
            threads = sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #"))
                .Select(s => (double)(s.Value ?? 0)).ToArray();

        double? temp = Live(Val(sensors, SensorType.Temperature, "Core (Tctl/Tdie)") ?? Val(sensors, SensorType.Temperature, "Tctl")
                       ?? Val(sensors, SensorType.Temperature, "CPU Package") ?? Val(sensors, SensorType.Temperature, "Package"));
        var tempSource = "Tctl/Tdie";
        if (temp == null)
        {
            temp = _hp.Available ? _hp.Temperature() : null;
            tempSource = "the HP BIOS sensor";
            if (temp == null) { temp = AcpiZone(); tempSource = "the ACPI thermal zone, an approximation"; }
            if (temp == null) tempSource = "unavailable";
            if (_elevated && !PawnIoInstalled()) notes.Add(new Note("pawnio", "CPU die temperature and package power need the PawnIO driver, a small signed driver that sensor tools use to read the processor. Without it the temperature shown is an estimate.", "installPawnIO"));
        }

        var clocks = sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #") && s.Value > 0)
            .Select(s => (double)s.Value!).ToArray();
        double? clockAvg = clocks.Length > 0 ? clocks.Average() : null;
        double? clockMax = clocks.Length > 0 ? clocks.Max() : null;
        if (clockAvg == null && _cpuPerf != null && _baseClock > 0)
        {
            // Same method Task Manager uses for "Speed" when it has no driver access.
            try { clockAvg = _cpuPerf.NextValue() / 100.0 * _baseClock; } catch { }
        }

        var power = Live(Val(sensors, SensorType.Power, "Package") ?? Val(sensors, SensorType.Power, "CPU Package"));
        return new CpuInfo(name, Math.Round(total, 1), threads.Select(t => Math.Round(t, 1)).ToArray(), R(temp), tempSource, R(clockAvg), R(clockMax), R(power, 1));
    }

    private static bool PawnIoInstalled() =>
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "PawnIO.sys"))
        || Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") != null;

    /// <summary>Re-open LibreHardwareMonitor so it picks up a driver installed while Thermal was running.</summary>
    public void Reopen() => _reopen = true;
    private volatile bool _reopen;

    private static double? AcpiZone()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            var temps = s.Get().Cast<ManagementBaseObject>().Select(o => Convert.ToDouble(o["CurrentTemperature"]) / 10 - 273.15).Where(t => t is > 25 and < 115).ToList();
            return temps.Count > 0 ? temps.Max() : null;
        }
        catch { return null; }
    }

    // ---------- GPUs ----------

    private GpuInfo[] BuildGpus()
    {
        var list = new List<GpuInfo>();
        var hp = _hp.Available ? _hp.FanLevels() : null;
        foreach (var a in _adapters)
        {
            var integrated = a.DedicatedVideo < 1UL << 30;
            var hw = _computer.Hardware.FirstOrDefault(h =>
                h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && Similar(h.Name, a.Name));
            var s = hw?.Sensors ?? Array.Empty<ISensor>();
            var isNvidia = a.VendorId == 0x10DE;
            var polled = _gpuOpen && !(isNvidia && !PollDiscreteGpu);

            var engines = _procs.AdapterEngines.GetValueOrDefault(a.Luid) ?? new Dictionary<string, double>();
            var engineArr = engines.OrderByDescending(e => e.Value).Select(e => new EngineLoad(e.Key, Math.Round(e.Value, 1))).ToArray();
            var counterLoad = engines.Count > 0 ? engines.Values.Max() : 0;

            double? load = polled ? Val(s, SensorType.Load, "GPU Core") ?? Val(s, SensorType.Load, "D3D 3D") : null;
            load ??= counterLoad;

            var mem = _procs.AdapterMemory.GetValueOrDefault(a.Luid);
            double? memUsed = polled ? MB(Val(s, SensorType.SmallData, "GPU Memory Used")) : null;
            double? memTotal = polled ? MB(Val(s, SensorType.SmallData, "GPU Memory Total")) : null;
            memUsed ??= mem.dedicated;
            memTotal ??= a.DedicatedVideo;

            double? fan = polled ? Val(s, SensorType.Fan, "GPU Fan") ?? s.FirstOrDefault(x => x.SensorType == SensorType.Fan)?.Value : null;
            if ((fan == null || fan == 0) && !integrated && hp != null) fan = hp[1];

            var id = $"gpu-{a.VendorId:x4}-{(integrated ? "i" : "d")}";
            _gpuLuids[id] = a.Luid;
            list.Add(new GpuInfo(
                id,
                a.Name,
                integrated ? "integrated" : "discrete",
                R(load, 1),
                polled ? R(Live(Val(s, SensorType.Temperature, "GPU Core") ?? Val(s, SensorType.Temperature, "GPU VR SoC"))) : null,
                polled ? R(Live(Val(s, SensorType.Temperature, "GPU Hot Spot"))) : null,
                polled ? R(Val(s, SensorType.Clock, "GPU Core")) : null,
                polled ? R(Val(s, SensorType.Clock, "GPU Memory")) : null,
                polled ? R(Val(s, SensorType.Power, "GPU Package") ?? Val(s, SensorType.Power, "GPU Core") ?? Val(s, SensorType.Power, "GPU SoC"), 1) : null,
                memUsed, memTotal, mem.shared,
                fan, polled ? R(Val(s, SensorType.Control, "GPU Fan")) : null,
                engineArr,
                integrated ? true : _dgpuOn ?? (polled || counterLoad > 0)));
        }
        // Discrete first: it is the one that heats up.
        return list.OrderBy(g => g.Kind == "integrated").ToArray();
    }

    private void RefreshAdaptersIfNeeded(List<ProcRow> procs)
    {
        // Re-enumerate if real work shows up on an adapter we don't know (a driver reset can change a LUID). The counters
        // also keep stale LUIDs with no work on them; those are ignored. Enumerating through DXGI wakes a sleeping
        // hybrid-graphics GPU, so never do it while the dedicated GPU is asleep.
        if (DateTime.UtcNow - _adaptersRefreshed < TimeSpan.FromSeconds(60) || _dgpuPower?.IsOn() == false) return;
        var known = _adapters.Select(a => a.Luid).ToHashSet();
        if (_procs.AdapterEngines.Any(kv => !known.Contains(kv.Key) && kv.Value.Values.Any(v => v > 1)))
        {
            _adapters = Dxgi.Enumerate();
            var discrete = _adapters.FirstOrDefault(a => a.DedicatedVideo >= 1UL << 30);
            if (discrete != null) _dgpuLuid = discrete.Luid;
            _adaptersRefreshed = DateTime.UtcNow;
        }
    }

    // ---------- Fans ----------

    private FanInfo[] BuildFans()
    {
        var fans = new List<FanInfo>();
        var hp = _hp.Available ? _hp.FanLevels() : null;
        if (hp != null)
        {
            fans.Add(new FanInfo("CPU fan", hp[0], null, "HP BIOS"));
            fans.Add(new FanInfo("GPU fan", hp[1], null, "HP BIOS"));
        }
        foreach (var hw in All())
        {
            if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel) continue;
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan && s.Value != null))
                fans.Add(new FanInfo(s.Name, R(s.Value), null, hw.Name));
        }
        return fans.ToArray();
    }

    // ---------- Memory, battery, disks, network ----------

    private static MemoryInfo BuildMemory()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref ms);
        var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        GetPerformanceInfo(ref pi, pi.cb);
        double page = (double)pi.PageSize;
        return new MemoryInfo(
            ms.ullTotalPhys - ms.ullAvailPhys, ms.ullTotalPhys,
            (double)pi.CommitTotal * page, (double)pi.CommitLimit * page, (double)pi.SystemCache * page, null);
    }

    private BatteryInfo? BuildBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)) return null;
        var hw = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
        var s = hw?.Sensors ?? Array.Empty<ISensor>();
        var charge = Val(s, SensorType.Level, "Charge Level") ?? ps.BatteryLifePercent * 100;
        double? rate = Val(s, SensorType.Power, "Discharge Rate") is double d && d > 0 ? -d : Val(s, SensorType.Power, "Charge Rate");
        var wear = Live(Val(s, SensorType.Level, "Degradation Level"));
        double? left = ps.BatteryLifeRemaining > 0 ? ps.BatteryLifeRemaining / 60.0 : null;
        return new BatteryInfo(Math.Round(charge, 0), R(rate, 1), ps.PowerLineStatus == PowerLineStatus.Online, R(left), R(wear, 1));
    }

    private DiskInfo[] BuildDisks() =>
        _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage)
            .Select(h => new DiskInfo(h.Name,
                Val(h.Sensors, SensorType.Throughput, "Read Rate") ?? 0,
                Val(h.Sensors, SensorType.Throughput, "Write Rate") ?? 0,
                R(Val(h.Sensors, SensorType.Load, "Total Activity"), 1),
                R(Val(h.Sensors, SensorType.Temperature, "Temperature") ?? h.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature)?.Value)))
            .ToArray();

    private NetInfo[] BuildNet() =>
        _computer.Hardware.Where(h => h.HardwareType == HardwareType.Network)
            .Select(h => new NetInfo(h.Name, Val(h.Sensors, SensorType.Throughput, "Upload Speed") ?? 0, Val(h.Sensors, SensorType.Throughput, "Download Speed") ?? 0))
            .Where(n => !n.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) && !n.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n.Up + n.Down)
            .ToArray();

    private SensorInfo[] BuildSensors() =>
        All().SelectMany(h => h.Sensors.Select(s => new SensorInfo(h.Name, s.SensorType.ToString(), s.Name, R(s.Value, 2), R(s.Min, 2), R(s.Max, 2))))
            .ToArray();

    // ---------- Actions ----------

    public string? Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    // ---------- helpers ----------

    private IEnumerable<IHardware> All() =>
        _computer.Hardware.SelectMany(h => new[] { h }.Concat(h.SubHardware));

    private static double? Val(IEnumerable<ISensor> sensors, SensorType type, string name) =>
        sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value != null && !float.IsNaN(s.Value.Value))?.Value;

    // Without the PawnIO driver LibreHardwareMonitor reports CPU temperature and power as exactly 0 rather than missing.
    private static double? Live(double? v) => v is > 0 ? v : null;

    private static double? MB(double? mb) => mb * 1024 * 1024;
    private static double? R(double? v, int digits = 0) => v == null || double.IsNaN(v.Value) ? null : Math.Round(v.Value, digits);

    private static bool Similar(string a, string b)
    {
        static string N(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()).Replace("tm", "");
        var x = N(a); var y = N(b);
        return x.Contains(y) || y.Contains(x) || (x.Contains("radeon") && y.Contains("radeon"));
    }

    private readonly Dictionary<int, string?> _paths = new();

    private string? PathOf(int pid)
    {
        if (_paths.TryGetValue(pid, out var p)) return p;
        try { using var proc = Process.GetProcessById(pid); p = proc.MainModule?.FileName; }
        catch { p = null; }
        return _paths[pid] = p;
    }

    private string Describe(int pid)
    {
        if (_descriptions.TryGetValue(pid, out var d)) return d;
        try
        {
            using var p = Process.GetProcessById(pid);
            var file = p.MainModule?.FileName;
            d = file != null ? FileVersionInfo.GetVersionInfo(file).FileDescription ?? "" : "";
        }
        catch { d = ""; }
        return _descriptions[pid] = d.Trim();
    }

    private void PruneDescriptions(List<ProcRow> procs)
    {
        if (_descriptions.Count < procs.Count * 2) return;
        var live = procs.Select(p => p.Pid).ToHashSet();
        foreach (var k in _descriptions.Keys.Where(k => !live.Contains(k)).ToList()) _descriptions.Remove(k);
        foreach (var k in _paths.Keys.Where(k => !live.Contains(k)).ToList()) _paths.Remove(k);
    }

    private static T? Wmi<T>(string query, Func<ManagementBaseObject, T> pick)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            var o = s.Get().Cast<ManagementBaseObject>().FirstOrDefault();
            return o == null ? default : pick(o);
        }
        catch { return default; }
    }

    public void Dispose()
    {
        _computer.Close();
        _hp.Dispose();
        _cpuPerf?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("psapi.dll")] private static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION p, uint size);
}
