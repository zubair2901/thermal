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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // MSBuild's %(RecursiveDir) keeps Windows separators in resource names; index them by URL path.
    private static readonly Dictionary<string, string> Resources = Assembly.GetExecutingAssembly()
        .GetManifestResourceNames().ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x16, 0x1B, 0x26) };
    private Collector? _collector;
    private System.Threading.Timer? _timer;
    private int _busy;
    private bool _ready;

    public MainForm()
    {
        Text = "Thermal";
        BackColor = Color.FromArgb(0x16, 0x1B, 0x26);
        ClientSize = new Size(1360, 860);
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(_web);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        var caption = 0x00261B16; // COLORREF (BGR) of --ink-0
        DwmSetWindowAttribute(Handle, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
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

        // Opening LibreHardwareMonitor takes a second or two (driver + SMBus probing); keep it off the UI thread.
        _ = Task.Run(() =>
        {
            _collector = new Collector();
            _timer = new System.Threading.Timer(_ => Tick(), null, 0, 1000);
        });
    }

    private void Tick()
    {
        if (_collector == null || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var snap = _collector.Collect();
            var json = JsonSerializer.Serialize(new { type = "snapshot", snapshot = snap }, Json);
            if (_ready && IsHandleCreated && !IsDisposed)
                BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch (Exception ex)
        {
            Log(ex);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                break;
            case "pollDgpu":
                if (_collector != null) _collector.PollDiscreteGpu = root.GetProperty("value").GetBoolean();
                break;
            case "kill":
                var pid = root.GetProperty("pid").GetInt32();
                var error = _collector?.Kill(pid);
                _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "killResult", pid, error }, Json));
                break;
        }
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer?.Dispose();
        _collector?.Dispose();
        base.OnFormClosed(e);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
