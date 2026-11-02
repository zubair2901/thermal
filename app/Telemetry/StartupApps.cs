using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Thermal.Telemetry;

public record StartupApp(string Id, string Name, bool Enabled, bool Running, long Bytes);

/// <summary>
/// The list behind Task Manager's Startup apps page. Run keys and Startup folders are switched through the
/// StartupApproved keys (first byte even = on, odd = off), Store apps through their startup task's State (1 off, 2 on).
/// </summary>
internal static class StartupApps
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private const string Tasks = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";

    public static StartupApp[] List(IReadOnlyDictionary<string, (long bytes, string? path)> running)
    {
        var apps = new List<StartupApp>();
        void Add(string id, string name, string? exe, bool on)
        {
            var path = exe == null ? null : Environment.ExpandEnvironmentVariables(exe);
            var stem = Path.GetFileNameWithoutExtension(path) ?? "";
            if (path != null && GpuPreference.IsSystem(stem, path)) return;
            var live = running.TryGetValue(stem, out var r);
            apps.Add(new StartupApp(id, Describe(path) ?? name, on, live, live ? r.bytes : 0));
        }

        foreach (var (hive, tag, key, approved) in new[] { (Registry.CurrentUser, "HKCU", Run, "Run"), (Registry.LocalMachine, "HKLM", Run, "Run"), (Registry.LocalMachine, "HKLM", Run32, "Run32") })
        {
            using var k = hive.OpenSubKey(key);
            if (k == null) continue;
            foreach (var name in k.GetValueNames().Where(n => n != ""))
                Add($"run|{tag}|{approved}|{name}", name, ExeOf(k.GetValue(name) as string ?? ""), IsOn(hive, approved, name));
        }

        foreach (var (hive, tag, folder) in new[] { (Registry.CurrentUser, "HKCU", Environment.SpecialFolder.Startup), (Registry.LocalMachine, "HKLM", Environment.SpecialFolder.CommonStartup) })
        {
            var dir = Environment.GetFolderPath(folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileName(file);
                Add($"run|{tag}|StartupFolder|{name}", Path.GetFileNameWithoutExtension(file), TargetOf(file), IsOn(hive, "StartupFolder", name));
            }
        }

        using var tasks = Registry.CurrentUser.OpenSubKey(Tasks);
        if (tasks != null)
        {
            var names = StoreAppNames();
            var byPackage = new Dictionary<string, StartupApp>();
            foreach (var family in tasks.GetSubKeyNames())
            {
                using var pkg = tasks.OpenSubKey(family);
                foreach (var id in pkg?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var t = pkg!.OpenSubKey(id);
                    if (t?.GetValue("State") is not int state || state is not (1 or 2)) continue;
                    // Most packages have one task; a package with several is one row, on if any task is on.
                    var live = RunningPackage(running, family);
                    var row = new StartupApp($"store|{family}", names.GetValueOrDefault(family) ?? Pretty(family), state == 2, live != null, live ?? 0);
                    byPackage[family] = byPackage.TryGetValue(family, out var cur) ? row with { Enabled = cur.Enabled || row.Enabled } : row;
                }
            }
            apps.AddRange(byPackage.Values);
        }

        return apps.GroupBy(a => a.Id).Select(g => g.First())
            .OrderByDescending(a => a.Enabled).ThenByDescending(a => a.Bytes).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? Set(string id, bool on)
    {
        try
        {
            var p = id.Split('|', 4);
            if (p[0] == "store")
            {
                using var tasks = Registry.CurrentUser.OpenSubKey($@"{Tasks}\{p[1]}");
                foreach (var name in tasks?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var t = tasks!.OpenSubKey(name, true);
                    if (t?.GetValue("State") is 1 or 2) t!.SetValue("State", on ? 2 : 1, RegistryValueKind.DWord);
                }
                return null;
            }
            var hive = p[1] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var k = hive.CreateSubKey(Approved + p[2]);
            var data = new byte[12];
            data[0] = (byte)(on ? 2 : 3);
            if (!on) BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
            k.SetValue(p[3], data, RegistryValueKind.Binary);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    private static bool IsOn(RegistryKey hive, string approved, string name)
    {
        using var k = hive.OpenSubKey(Approved + approved);
        return k?.GetValue(name) is not byte[] { Length: > 0 } d || (d[0] & 1) == 0;
    }

    private static string? ExeOf(string command)
    {
        // Squirrel apps (Discord, Teams and others) start through a shared Update.exe that names the real program.
        var squirrel = Regex.Match(command, @"--processStart(?:AndWait)?[ =]""?([^"" ]+\.exe)", RegexOptions.IgnoreCase);
        if (squirrel.Success) return squirrel.Groups[1].Value;
        var c = command.Trim();
        if (c.StartsWith('"')) return c.Length > 1 && c.IndexOf('"', 1) is var q and > 0 ? c[1..q] : null;
        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? c[..(exe + 4)] : null;
    }

    private static string? TargetOf(string file)
    {
        if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return file;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            return shell.CreateShortcut(file).TargetPath as string;
        }
        catch { return null; }
    }

    private static string? Describe(string? path)
    {
        try { return path != null && File.Exists(path) && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d ? d.Trim() : null; }
        catch { return null; }
    }

    private static long? RunningPackage(IReadOnlyDictionary<string, (long bytes, string? path)> running, string family)
    {
        var folder = $@"\WindowsApps\{family[..Math.Max(0, family.IndexOf('_'))]}_";
        var hits = running.Values.Where(r => r.path?.Contains(folder, StringComparison.OrdinalIgnoreCase) == true).ToList();
        return hits.Count > 0 ? hits.Sum(r => r.bytes) : null;
    }

    private static string Pretty(string family)
    {
        var name = family.Split('_')[0];
        return name[(name.IndexOf('.') + 1)..];
    }

    /// <summary>Display names of Store apps, keyed by package family, from the shell's Apps folder.</summary>
    private static Dictionary<string, string> StoreAppNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (var item in shell.NameSpace("shell:AppsFolder").Items())
            {
                string id = item.Path;
                if (id.IndexOf('!') is var bang and > 0) names.TryAdd(id[..bang], (string)item.Name);
            }
        }
        catch (Exception e) { MainForm.Log(e); }
        return names;
    }
}
