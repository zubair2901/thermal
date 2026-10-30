namespace Thermal.Telemetry;

// Shape mirrors ui/src/types.ts. Serialized camelCase.

public record MachineInfo(string Name, string Model, string Os, long UptimeSec, bool Elevated);
public record CpuInfo(string Name, double Load, double[] Cores, double? Temp, string TempSource, double? ClockAvg, double? ClockMax, double? Power);
public record EngineLoad(string Name, double Load);
public record GpuInfo(
    string Id, string Name, string Kind, double? Load, double? Temp, double? Hotspot, double? ClockCore, double? ClockMem,
    double? Power, double? MemUsed, double? MemTotal, double? MemShared, double? FanRpm, double? FanPct, EngineLoad[] Engines, bool Active);
public record FanInfo(string Name, double? Rpm, double? Pct, string Source);
public record MemoryInfo(double Used, double Total, double Committed, double CommitLimit, double Cached, double? Compressed);
public record BatteryInfo(double Charge, double? RateW, bool OnAc, double? MinutesLeft, double? Wear);
public record DiskInfo(string Name, double Read, double Write, double? Activity, double? Temp);
public record NetInfo(string Name, double Up, double Down);
public record ProcInfo(int Pid, string Name, string Title, double Cpu, long Ram, long Commit, double Gpu, string GpuName, string GpuEngine, long Vram, long Io, int Threads);
public record Note(string Id, string Text, string? Action = null);
public record SensorInfo(string Hw, string Type, string Name, double? Value, double? Min, double? Max);

public record Snapshot(
    long T, MachineInfo Machine, CpuInfo Cpu, GpuInfo[] Gpus, FanInfo[] Fans, MemoryInfo Memory, BatteryInfo? Battery,
    DiskInfo[] Disks, NetInfo[] Net, ProcInfo[] Processes, SensorInfo[] Sensors, Note[] Notes,
    FpsInfo? Fps = null, RecordingInfo? Recording = null, PowerInfo? Power = null, bool GpuSensorsConnected = true);
