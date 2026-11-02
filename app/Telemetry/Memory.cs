using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Thermal.Telemetry;

public record MemoryRole(string Name, int Count, long Bytes);
/// <summary>One app with its helper processes. Kind: "browser", "webapp" (built on a browser engine) or "app". Growth is 0 unless it keeps climbing.</summary>
public record MemoryApp(string Name, string Title, long Bytes, int Processes, string Kind, MemoryRole[] Roles, long Growth, int GrowthMinutes, string? Path, string? AppId, bool CanClose);
/// <summary>Where physical memory is, in bytes. Pressure: "fine", "tight" or "low".</summary>
public record MemoryDetail(long Installed, long Reserved, long Available, long AppBytes, long Compressed, long Kernel, long Graphics, long Other, long Cached, long Free, string Pressure, MemoryApp[] Apps);

/// <summary>
/// Explains memory use the way Task Manager's list can't: helper processes count towards the app that started them,
/// browser-engine processes are split by what they do, and the memory no process owns is itemised.
/// </summary>
internal sealed class MemoryModel
{
    private sealed record Proc(string Name, string Role, string? HostExe, string? Path);

    // Runtimes that apps embed. Their processes belong to the app that started them, like Task Manager shows it.
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase) { "msedgewebview2", "QtWebEngineProcess", "CefSharp.BrowserSubprocess" };
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "brave", "vivaldi" };
    private const string SingleInstance = "systemdiagnosticsperfcounterlibsingleinstance";
    private const long MB = 1 << 20;

    private readonly CategoryReader _counters = new("Memory");
    private readonly Dictionary<int, Proc> _cmd = new();
    private readonly Dictionary<int, string?> _appIds = new();
    private readonly Dictionary<string, List<long>> _trend = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _trendAt = DateTime.MinValue;

    /// <summary>Latest size and path per app, for matching startup entries to what's running.</summary>
    public IReadOnlyDictionary<string, (long bytes, string? path)> Running { get; private set; } = new Dictionary<string, (long, string?)>();

    /// <summary>Set each process's owning app and, for browser-engine processes, what it does.</summary>
    public void Attribute(List<ProcRow> procs)
    {
        var byPid = procs.ToDictionary(p => p.Pid);
        foreach (var p in procs)
        {
            var c = CommandInfo(p);
            p.Role = c.Role;
            p.Owner = Hosts.Contains(p.Name) ? OwnerOf(p, byPid) ?? ExeStem(c.HostExe) ?? p.Name : p.Name;
            p.OwnerPath = c.Path;
        }
        // An embedded engine sits under the program that started it, not its own runtime folder.
        foreach (var p in procs.Where(p => Hosts.Contains(p.Name) && p.Owner != p.Name))
            p.OwnerPath = OwnerRoot(p, byPid)?.OwnerPath ?? p.OwnerPath;
        foreach (var pid in _cmd.Keys.Where(k => !byPid.ContainsKey(k)).ToList())
        {
            _cmd.Remove(pid);
            _appIds.Remove(pid);
        }
    }

    private static string? OwnerOf(ProcRow p, Dictionary<int, ProcRow> byPid)
    {
        var root = OwnerRoot(p, byPid);
        return root == null || GpuPreference.IsSystem(root.Name, null) ? null : root.Name;
    }

    private static ProcRow? OwnerRoot(ProcRow p, Dictionary<int, ProcRow> byPid)
    {
        var cur = p;
        for (var i = 0; i < 8 && byPid.TryGetValue(cur.Parent, out var parent); i++)
        {
            if (!Hosts.Contains(parent.Name)) return parent;
            cur = parent;
        }
        return null;
    }

    private static string? ExeStem(string? exe) => exe == null ? null : Path.GetFileNameWithoutExtension(exe);

    private Proc CommandInfo(ProcRow p)
    {
        if (_cmd.TryGetValue(p.Pid, out var c) && c.Name == p.Name) return c;
        var cmd = CommandLine(p.Pid);
        c = new Proc(p.Name, RoleOf(cmd), Arg(cmd, "--webview-exe-name="), GpuPreference.ImagePath(p.Pid));
        return _cmd[p.Pid] = c;
    }

    /// <summary>Chromium-family apps say what each process does on its command line.</summary>
    private static string RoleOf(string? cmd) => Arg(cmd, "--type=") switch
    {
        null => "",
        "renderer" => cmd!.Contains("--extension-process") ? "Extensions" : "Pages",
        "gpu-process" => "Graphics",
        _ => "Helpers",
    };

    private static string? Arg(string? cmd, string name)
    {
        var i = cmd?.IndexOf(name, StringComparison.Ordinal) ?? -1;
        if (i < 0) return null;
        var v = cmd![(i + name.Length)..];
        var end = v.IndexOf(' ');
        return (end < 0 ? v : v[..end]).Trim('"');
    }

    public MemoryDetail Build(MemoryInfo m, List<ProcRow> procs, IEnumerable<(long dedicated, long shared)> adapters, Func<int, string> describe, Func<int, string?> pathOf)
    {
        var (now, _) = _counters.Read();
        long Counter(string name) => CategoryReader.Raw(now, name, SingleInstance);

        var usable = (long)m.Total;
        var used = (long)m.Used;
        var available = usable - used;
        GetPhysicallyInstalledSystemMemory(out var installedKb);
        var installed = Math.Max(usable, installedKb * 1024);

        var compressed = procs.Where(p => p.Name == "Memory Compression").Sum(p => p.Ram);
        var apps = procs.Sum(p => p.Ram) - compressed;
        var kernel = Counter("Pool Nonpaged Bytes") + Counter("Pool Paged Resident Bytes") + Counter("System Driver Resident Bytes") + Counter("System Code Resident Bytes");
        // Part of GPU shared memory can already sit in an app's working set, so it only fills what the others leave.
        var graphics = Math.Min(adapters.Sum(a => a.shared), Math.Max(0, used - apps - compressed - kernel));
        var other = Math.Max(0, used - apps - compressed - kernel - graphics);
        var free = Counter("Free & Zero Page List Bytes");

        var freeShare = (double)available / Math.Max(1, usable);
        var commit = m.Committed / Math.Max(1, m.CommitLimit);
        var pressure = freeShare < 0.08 || commit > 0.92 ? "low" : freeShare <= 0.2 || commit > 0.85 ? "tight" : "fine";

        return new MemoryDetail(installed, installed - usable, available, apps, compressed, kernel, graphics, other,
            Math.Max(0, available - free), free, pressure, BuildApps(procs, describe, pathOf));
    }

    private MemoryApp[] BuildApps(List<ProcRow> procs, Func<int, string> describe, Func<int, string?> pathOf)
    {
        var groups = procs.Where(p => p.Name != "Memory Compression").GroupBy(p => $"{p.Owner}|{p.OwnerPath}", StringComparer.OrdinalIgnoreCase).ToList();
        Track(groups);
        var running = new Dictionary<string, (long, string?)>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MemoryApp>();
        foreach (var grp in groups.OrderByDescending(g => g.Sum(p => p.Ram)))
        {
            var name = grp.First().Owner;
            var root = grp.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? grp.First();
            var bytes = grp.Sum(p => p.Ram);
            var path = pathOf(root.Pid);
            running[name] = (running.GetValueOrDefault(name).Item1 + bytes, path);
            if (result.Count >= 10 || bytes < 100 * MB) continue;

            var engine = grp.Any(p => p.Role != "");
            var roles = !engine ? Array.Empty<MemoryRole>() : grp.GroupBy(p => p.Role == "" ? "Main app" : p.Role)
                .Select(r => new MemoryRole(r.Key, r.Count(), r.Sum(p => p.Ram)))
                .OrderByDescending(r => r.Bytes).ToArray();
            var (growth, minutes) = Growth(grp.Key);
            var packaged = path != null && GpuPreference.IsPackaged(path);
            if (packaged && !_appIds.ContainsKey(root.Pid)) _appIds[root.Pid] = GpuPreference.AppIdOf(root.Pid);
            var title = describe(root.Pid);
            // Without a file description, "WhatsApp.Root" reads better as "WhatsApp".
            result.Add(new MemoryApp(name, title == "" || title == name ? name.Split('.')[0] : title, bytes, grp.Count(),
                !engine ? "app" : Browsers.Contains(name) ? "browser" : "webapp", roles, growth, minutes, path,
                packaged ? _appIds[root.Pid] : null,
                path != null && !GpuPreference.IsSystem(root.Name, path) && root.Name != "Thermal" && !IsService(root.Pid)));
        }
        Running = running;
        return result.ToArray();
    }

    private static bool IsService(int pid) => !ProcessIdToSessionId(pid, out var session) || session == 0;

    /// <summary>One sample a minute of each app's private memory, over the last half hour.</summary>
    private void Track(List<IGrouping<string, ProcRow>> groups)
    {
        var now = DateTime.UtcNow;
        if (now - _trendAt < TimeSpan.FromMinutes(1)) return;
        // After sleep or a screen-off pause the samples are no longer a minute apart.
        if (now - _trendAt > TimeSpan.FromMinutes(3)) _trend.Clear();
        _trendAt = now;
        var live = groups.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _trend.Keys.Where(k => !live.Contains(k)).ToList()) _trend.Remove(k);
        foreach (var g in groups)
        {
            if (!_trend.TryGetValue(g.Key, out var list)) _trend[g.Key] = list = new List<long>();
            list.Add(g.Sum(p => p.Commit));
            if (list.Count > 30) list.RemoveAt(0);
        }
    }

    /// <summary>A steady climb, not a spike: at least 15 minutes, 300 MB and 20% up, rising in most samples and still at its peak.</summary>
    private (long bytes, int minutes) Growth(string app)
    {
        if (!_trend.TryGetValue(app, out var s) || s.Count < 16) return (0, 0);
        var rise = s[^1] - s[0];
        if (rise < 300 * MB || rise < s[0] / 5 || s[^1] < s.Max() * 0.97) return (0, 0);
        var rising = s.Zip(s.Skip(1)).Count(x => x.Second >= x.First);
        return rising >= (s.Count - 1) * 3 / 4 ? (rise, s.Count - 1) : (0, 0);
    }

    private static string? CommandLine(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            if (NtQueryInformationProcess(h, 60 /* ProcessCommandLineInformation */, buffer, 8192, out _) != 0) return null;
            // A UNICODE_STRING header whose text follows it in the same buffer.
            var length = Marshal.ReadInt16(buffer) & 0xFFFF;
            return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, IntPtr.Size), length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out long kilobytes);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int cls, IntPtr buffer, int length, out int returned);
}
