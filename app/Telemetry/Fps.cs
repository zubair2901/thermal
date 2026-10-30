using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Thermal.Telemetry;

public record FpsInfo(int Pid, string Name, double Fps, double FrameTimeMs, double? Low1);

/// <summary>
/// Counts frame presents per process from the DXGI and D3D9 ETW providers (the same events PresentMon reads) and
/// reports the foreground app's frame rate. Covers DirectX 9–12 games, and Vulkan/OpenGL games presented through DXGI.
/// </summary>
internal sealed class FpsCounter : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const double KeepMs = 10_000;

    private readonly TraceEventSession? _session;
    private readonly Dictionary<int, Queue<double>> _presents = new();
    private readonly object _lock = new();
    private readonly int _self = Environment.ProcessId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _clockOffset = double.NaN; // event clock minus our clock

    public string? Error { get; }

    public FpsCounter()
    {
        try
        {
            _session = new TraceEventSession("Thermal-FrameRate") { StopOnDispose = true };
            _session.EnableProvider(Dxgi, TraceEventLevel.Verbose);
            _session.EnableProvider(D3D9, TraceEventLevel.Verbose);
            _session.Source.AllEvents += OnEvent;
            new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception e) { MainForm.Log(e); }
            }) { IsBackground = true, Name = "ETW presents" }.Start();
        }
        catch (Exception e)
        {
            Error = e.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        var id = (int)e.ID;
        if (!(e.ProviderGuid == Dxgi && id == DxgiPresentStart) && !(e.ProviderGuid == D3D9 && id == D3D9PresentStart)) return;
        var pid = e.ProcessID;
        if (pid == _self) return;
        var t = e.TimeStampRelativeMSec;
        lock (_lock)
        {
            if (double.IsNaN(_clockOffset)) _clockOffset = t - _clock.Elapsed.TotalMilliseconds;
            if (!_presents.TryGetValue(pid, out var q)) _presents[pid] = q = new Queue<double>();
            q.Enqueue(t);
            while (q.Count > 0 && t - q.Peek() > KeepMs) q.Dequeue();
        }
    }

    /// <summary>Frame rate of the process that owns the foreground window, or null if it isn't presenting frames.</summary>
    public FpsInfo? Sample()
    {
        if (_session == null) return null;
        var pid = ForegroundPid();
        double[] times;
        lock (_lock)
        {
            var now = _clock.Elapsed.TotalMilliseconds + (double.IsNaN(_clockOffset) ? 0 : _clockOffset);
            foreach (var k in _presents.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() > KeepMs).Select(kv => kv.Key).ToList())
                _presents.Remove(k);
            if (pid == 0 || !_presents.TryGetValue(pid, out var q) || q.Count < 2 || now - q.Last() > 2500) return null; // real-time ETW delivers in ~1 s batches
            times = q.ToArray();
        }

        var last = times[^1];
        var inLastSecond = times.Count(t => last - t < 1000);
        var gaps = new double[times.Length - 1];
        for (var i = 1; i < times.Length; i++) gaps[i - 1] = times[i] - times[i - 1];
        double? low1 = null;
        if (gaps.Length >= 100)
        {
            Array.Sort(gaps);
            var p99 = gaps[(int)Math.Floor(gaps.Length * 0.99)];
            if (p99 > 0) low1 = Math.Round(1000 / p99);
        }
        var fps = inLastSecond;
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { name = ""; }
        return new FpsInfo(pid, name, fps, fps > 0 ? Math.Round(1000.0 / fps, 1) : 0, low1);
    }

    private static int ForegroundPid()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    public void Dispose() => _session?.Dispose();

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
