using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Thermal.Telemetry;

namespace Thermal;

internal sealed class MainForm : Form
{
    private const string Host = "https://thermal.app/";
    private const int HotkeyOverlay = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // MSBuild's %(RecursiveDir) keeps Windows separators in resource names; index them by URL path.
    private static readonly Dictionary<string, string> Resources = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x16, 0x1B, 0x26) };
    private readonly Settings _settings = Settings.Load();
    private readonly bool _startHidden;
    private readonly Tray _tray;
    private readonly Alerts _alerts = new();
    private readonly Recorder _recorder = new();
    private readonly OverlayWindow _overlay = new();
    private readonly WidgetWindow _widget;
    private Collector? _collector;
    private FpsCounter? _fps;
    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _ready, _exiting, _shownOnce, _trayHintShown;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        Text = "Thermal";
        BackColor = Color.FromArgb(0x16, 0x1B, 0x26);
        MinimumSize = new Size(980, 640);
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        if (_settings.WindowBounds is [var x, var y, var w, var h] && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(x, y, w, h))))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, w, h);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1360, 860);
        }
        Controls.Add(_web);

        _tray = new Tray(Icon!, ShowMain, ExitApp);
        _tray.Overlay.CheckedChanged += (_, _) => { if (_settings.OverlayEnabled != _tray.Overlay.Checked) { _settings.OverlayEnabled = _tray.Overlay.Checked; ApplySettings(); } };
        _tray.Widget.CheckedChanged += (_, _) => { if (_settings.WidgetEnabled != _tray.Widget.Checked) { _settings.WidgetEnabled = _tray.Widget.Checked; ApplySettings(); } };
        _tray.Record.Click += (_, _) => ToggleRecording();
        _widget = new WidgetWindow(_settings, ShowMain, () => { _settings.WidgetEnabled = false; ApplySettings(); });
    }

    // Start hidden (from the logon task) without losing the window handle that hotkeys and BeginInvoke need.
    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !_shownOnce)
        {
            _shownOnce = true;
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var caption = 0x00261B16; // COLORREF (BGR) of --ink-0
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thermal", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("THERMAL_DEVTOOLS") == "1";
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ServeEmbedded;
            core.WebMessageReceived += OnMessage;
            var dev = Environment.GetEnvironmentVariable("THERMAL_DEV_URL");
            core.Navigate(string.IsNullOrEmpty(dev) ? Host + "index.html" : dev);
        }
        catch (Exception ex) { Log(ex); }

        try { ApplySettings(save: false); }
        catch (Exception ex) { Log(ex); }

        // Opening LibreHardwareMonitor takes a second or two (driver + SMBus probing); keep it off the UI thread.
        _ = Task.Run(() =>
        {
            _collector = new Collector { PollDiscreteGpu = _settings.PollDgpu };
            _fps = new FpsCounter();
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
        });
    }

    private void Tick()
    {
        if (_collector == null || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var fps = _fps?.Sample();
            var snap = _collector.Collect() with { Fps = fps };
            _recorder.Add(snap, fps);
            snap = snap with { Recording = _recorder.Info() };
            var alerts = _alerts.Evaluate(snap, _settings).ToList();
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() => Render(snap, alerts));
        }
        catch (Exception ex) { Log(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Render(Snapshot snap, List<string> alerts)
    {
        try { RenderCore(snap, alerts); }
        catch (Exception ex) { Log(ex); }
    }

    private void RenderCore(Snapshot snap, List<string> alerts)
    {
        // Serialising ~500 processes every second is the main cost of the window; skip it while it's hidden.
        if (_ready && Visible && WindowState != FormWindowState.Minimized)
            Post(new { type = "snapshot", snapshot = snap });
        if (_settings.OverlayEnabled) _overlay.Update(snap, _settings);
        _widget.Update(snap);
        var gpu = snap.Gpus.FirstOrDefault(g => g.Kind == "discrete") ?? snap.Gpus.FirstOrDefault();
        _tray.Update(snap.Cpu.Temp, $"Thermal\nCPU {Deg(snap.Cpu.Temp)}, {snap.Cpu.Load:0}% load" + (gpu != null ? $"\nGPU {Deg(gpu.Temp)}, {gpu.Load:0}% load" : ""), _settings.TrayTemperature);
        _tray.Record.Text = _recorder.Active ? "Stop recording" : "Start recording";
        foreach (var a in alerts) _tray.Notify("Running hot", a);
    }

    private static string Deg(double? t) => t == null ? "–" : $"{t:0} °C";

    // ---------- Messages from the UI ----------

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                PostSettings();
                break;
            case "settings":
                var incoming = root.GetProperty("settings").Deserialize<Settings>(Json);
                if (incoming == null) break;
                // Copy into the live instance: the widget and alerts hold a reference to it.
                foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanWrite && p.Name is not nameof(Settings.WindowBounds) and not nameof(Settings.WidgetX) and not nameof(Settings.WidgetY)))
                    p.SetValue(_settings, p.GetValue(incoming));
                ApplySettings();
                break;
            case "kill":
                var pid = root.GetProperty("pid").GetInt32();
                Post(new { type = "killResult", pid, error = _collector?.Kill(pid) });
                break;
            case "record":
                if (root.GetProperty("value").GetBoolean() != _recorder.Active) ToggleRecording();
                break;
            case "sessions":
                PostSessions();
                break;
            case "open":
                var path = root.GetProperty("path").GetString();
                if (path != null && (path.StartsWith(Recorder.Folder, StringComparison.OrdinalIgnoreCase)))
                    Process.Start(new ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
                break;
            case "gpuPref":
            {
                var key = AppId(root) ?? root.GetProperty("path").GetString()!;
                string? err = null;
                try { GpuPreference.Set(key, root.GetProperty("value").GetInt32()); } catch (Exception ex) { err = ex.Message; }
                Post(new { type = "appAction", name = root.GetProperty("name").GetString(), action = "gpuPref", error = err });
                break;
            }
            case "restartApp":
            {
                var n = root.GetProperty("name").GetString()!;
                var err = GpuPreference.Restart(n, root.GetProperty("path").GetString()!, AppId(root));
                Post(new { type = "appAction", name = n, action = "restart", error = err });
                break;
            }
            case "closeApp":
            {
                var n = root.GetProperty("name").GetString()!;
                Post(new { type = "appAction", name = n, action = "close", error = GpuPreference.Close(n) });
                break;
            }
            case "graphicsSettings":
                Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true });
                break;
            case "installPawnIO":
                _ = InstallPawnIo();
                break;
            case "link":
                var url = root.GetProperty("url").GetString();
                if (url != null && url.StartsWith("https://")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                break;
        }
    }

    private static string? AppId(JsonElement root) =>
        root.TryGetProperty("appId", out var id) && id.GetString() is { Length: > 0 } s ? s : null;

    private void Post(object msg)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Json)); }
        catch (Exception ex) { Log(ex); }
    }

    private void PostSettings() => Post(new
    {
        type = "settings",
        settings = _settings,
        fpsAvailable = _fps?.Error == null,
        sessionsFolder = Recorder.Folder,
        version = Application.ProductVersion.Split('+')[0],
    });

    private void PostSessions()
    {
        var files = Directory.Exists(Recorder.Folder)
            ? new DirectoryInfo(Recorder.Folder).GetFiles("session-*.csv").OrderByDescending(f => f.LastWriteTime).Take(30)
                .Select(f => new { name = f.Name, path = f.FullName, size = f.Length, modified = new DateTimeOffset(f.LastWriteTime).ToUnixTimeMilliseconds() })
                .ToArray()
            : Array.Empty<object>();
        Post(new { type = "sessions", files });
    }

    private void ToggleRecording()
    {
        if (_recorder.Active) _recorder.Stop(); else _recorder.Start();
        _tray.Record.Text = _recorder.Active ? "Stop recording" : "Start recording";
        PostSessions();
    }

    private async Task InstallPawnIo()
    {
        string message;
        var ok = false;
        try
        {
            var psi = new ProcessStartInfo("winget", "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            ok = p.ExitCode == 0 || output.Contains("already installed", StringComparison.OrdinalIgnoreCase);
            message = ok ? "PawnIO is installed. CPU readings switch over within a few seconds." : $"winget could not install PawnIO (exit code {p.ExitCode}). Download it from pawnio.eu instead.";
        }
        catch (Exception ex)
        {
            Log(ex);
            message = "winget isn't available on this PC. Download PawnIO from pawnio.eu, install it, then restart Thermal.";
        }
        if (ok) _collector?.Reopen();
        Post(new { type = "pawnio", ok, message });
    }

    // ---------- Settings ----------

    private void ApplySettings(bool save = true)
    {
        if (save) _settings.Save();
        if (_collector != null) _collector.PollDiscreteGpu = _settings.PollDgpu;

        if (!_settings.OverlayEnabled && _overlay.Visible) _overlay.Hide();
        if (_settings.WidgetEnabled && !_widget.Visible) { _widget.Place(); _widget.Show(); _widget.Draw(); }
        if (!_settings.WidgetEnabled && _widget.Visible) _widget.Hide();
        _widget.TopMost = _settings.WidgetOnTop;
        _tray.Overlay.Checked = _settings.OverlayEnabled;
        _tray.Widget.Checked = _settings.WidgetEnabled;

        UnregisterHotKey(Handle, HotkeyOverlay);
        if (ParseHotkey(_settings.OverlayHotkey) is var (mods, key))
            RegisterHotKey(Handle, HotkeyOverlay, mods | 0x4000 /* MOD_NOREPEAT */, key);

        if (save) StartupTask.Set(_settings.StartWithWindows);
        if (_ready) PostSettings();
    }

    private static (uint mods, uint key)? ParseHotkey(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "None") return null;
        uint mods = 0;
        var parts = s.Split('+');
        foreach (var part in parts[..^1])
            mods |= part switch { "Ctrl" => 0x2u, "Alt" => 0x1u, "Shift" => 0x4u, "Win" => 0x8u, _ => 0u };
        return Enum.TryParse<Keys>(parts[^1], out var k) ? (mods, (uint)k) : null;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 /* WM_HOTKEY */ && m.WParam == HotkeyOverlay)
        {
            _settings.OverlayEnabled = !_settings.OverlayEnabled;
            ApplySettings();
        }
        base.WndProc(ref m);
    }

    // ---------- Window lifecycle ----------

    public void ShowMain()
    {
        if (InvokeRequired) { BeginInvoke(ShowMain); return; }
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (WindowState == FormWindowState.Normal) _settings.WindowBounds = new[] { Left, Top, Width, Height };
        _settings.Save();
        if (!_exiting && _settings.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Info("Thermal is still running", "It keeps the overlay, widget and alerts going. Right-click the tray icon to exit.");
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer?.Dispose();
        _recorder.Stop();
        _fps?.Dispose();
        _collector?.Dispose();
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private void ServeEmbedded(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path == "") path = "index.html";
        var stream = Resources.TryGetValue("wwwroot/" + path, out var name)
            ? Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            : null;
        if (stream == null)
        {
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        var type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript",
            ".css" => "text/css",
            ".woff2" => "font/woff2",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}");
    }

    internal static void Trace(string line)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thermal");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "thermal.log"), $"{DateTime.Now:s} {line}\n");
        }
        catch { }
    }

    internal static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thermal");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:s} {ex}\n");
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

/// <summary>"Start with Windows" for an app that needs elevation: a logon task with highest privileges (the Run key can't elevate).</summary>
internal static class StartupTask
{
    private const string Name = "Thermal";

    public static void Set(bool enabled)
    {
        var exists = Run("/Query /TN " + Name) == 0;
        if (enabled == exists) return;
        Run(enabled
            ? $"/Create /F /TN {Name} /TR \"\\\"{Environment.ProcessPath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /IT"
            : $"/Delete /F /TN {Name}");
    }

    private static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(5000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
