using System.Diagnostics;
using Microsoft.Win32;

namespace Thermal.Telemetry;

/// <summary>
/// Per-app GPU choice, stored where Settings > System > Display > Graphics keeps it:
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences, data "GpuPreference=N;". The value name is the exe path for
/// desktop apps and the app user model ID ("PackageFamilyName!App") for Microsoft Store apps.
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

    public static bool IsPackaged(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var len = 1024;
            var sb = new System.Text.StringBuilder(len);
            return QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>App user model ID of a running Store app, e.g. "OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0!ChatGPT".</summary>
    public static string? AppIdOf(int pid)
    {
        var h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 512;
            var sb = new System.Text.StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, System.Text.StringBuilder id);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder path, ref int size);

    // Core Windows processes that can hold the GPU but can't be changed or closed.
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "csrss", "dwm", "winlogon", "wininit", "smss", "services", "lsass", "svchost",
        "fontdrvhost", "audiodg", "Memory Compression", "Idle", "explorer", "sihost", "ctfmon",
    };

    public static bool IsSystem(string name, string? path) =>
        SystemNames.Contains(name) || (path != null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase));

    /// <summary>Close every process of an app and start it again as the signed-in user, not elevated like Thermal.</summary>
    public static string? Restart(string name, string path, string? appId)
    {
        var error = Close(name, path);
        if (error != null) return error;
        try
        {
            // explorer.exe launches the program in the user's normal (non-administrator) session. Store apps start
            // through their app ID, the way the Start menu opens them.
            var target = appId != null ? $@"shell:AppsFolder\{appId}" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false });
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    /// <summary>With a path, only that program closes: unrelated programs can share a name (Claude Desktop and Claude Code do).</summary>
    public static string? Close(string name, string? path = null)
    {
        if (string.Equals(name, "Thermal", StringComparison.OrdinalIgnoreCase)) return "Thermal can't close itself here.";
        var failed = 0;
        var procs = Process.GetProcessesByName(name)
            .Where(p => path == null || string.Equals(ImagePath(p.Id), path, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var p in procs)
        {
            try { p.Kill(); p.WaitForExit(3000); }
            catch { failed++; }
            finally { p.Dispose(); }
        }
        return procs.Length == 0 ? null : failed == procs.Length ? $"Windows didn't let Thermal close {name}." : null;
    }
}
