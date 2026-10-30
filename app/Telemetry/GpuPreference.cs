using System.Diagnostics;
using Microsoft.Win32;

namespace Thermal.Telemetry;

/// <summary>
/// Per-app GPU choice, stored where Settings > System > Display > Graphics keeps it:
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value name = exe path, data "GpuPreference=N;".
/// 0 = let Windows decide, 1 = power saving (integrated), 2 = high performance (dedicated).
/// Apps read it when they start, so a change applies from the app's next launch.
/// </summary>
internal static class GpuPreference
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    public static int? Get(string path)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        if (k?.GetValue(path) is not string data) return null;
        foreach (var part in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && kv[0] == "GpuPreference" && int.TryParse(kv[1], out var v)) return v;
        }
        return null;
    }

    public static void Set(string path, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        // Keep any other DirectX options Windows stored for this app (e.g. SwapEffectUpgradeEnable).
        var parts = (k.GetValue(path) as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("GpuPreference=")).ToList();
        parts.Insert(0, $"GpuPreference={value}");
        k.SetValue(path, string.Join(";", parts) + ";");
    }

    /// <summary>Store apps keep their GPU choice by package, not exe path; they're changed in Windows Settings instead.</summary>
    public static bool IsPackaged(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    // Core Windows processes that can hold the GPU but can't be changed or closed.
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "csrss", "dwm", "winlogon", "wininit", "smss", "services", "lsass", "svchost",
        "fontdrvhost", "audiodg", "Memory Compression", "Idle", "explorer", "sihost", "ctfmon",
    };

    public static bool IsSystem(string name, string? path) =>
        SystemNames.Contains(name) || (path != null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));

    /// <summary>Close every process of an app and start it again as the signed-in user, not elevated like Thermal.</summary>
    public static string? Restart(string name, string path)
    {
        var error = Close(name);
        if (error != null) return error;
        try
        {
            // explorer.exe launches the program in the user's normal (non-administrator) session.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    public static string? Close(string name)
    {
        if (string.Equals(name, "Thermal", StringComparison.OrdinalIgnoreCase)) return "Thermal can't close itself here.";
        var failed = 0;
        var procs = Process.GetProcessesByName(name);
        foreach (var p in procs)
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        return procs.Length == 0 ? null : failed == procs.Length ? $"Windows didn't let Thermal close {name}." : null;
    }
}
