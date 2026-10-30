using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Thermal.Telemetry;

namespace Thermal;

/// <summary>Colours and type shared by the overlay, the widget and the tray icon. Mirrors ui/src/format.ts.</summary>
internal static class Look
{
    // Opaque: any text behind a see-through panel bleeds into the readings.
    public static readonly Color Panel = Color.FromArgb(255, 22, 27, 38);
    public static readonly Color Text = Color.FromArgb(228, 232, 240);
    public static readonly Color Muted = Color.FromArgb(141, 151, 171);
    public static readonly Color Faint = Color.FromArgb(95, 106, 128);
    public static readonly Color Track = Color.FromArgb(39, 48, 68);

    private static readonly (double at, Color c)[] Stops =
    {
        (0.0, Color.FromArgb(64, 120, 168)), (0.35, Color.FromArgb(79, 163, 217)), (0.62, Color.FromArgb(240, 176, 63)),
        (0.82, Color.FromArgb(236, 112, 54)), (1.0, Color.FromArgb(229, 72, 77)),
    };

    public static Color Heat(double x)
    {
        x = Math.Clamp(x, 0, 1);
        for (var i = 1; i < Stops.Length; i++)
        {
            if (x > Stops[i].at) continue;
            var (a0, c0) = Stops[i - 1];
            var (a1, c1) = Stops[i];
            var k = (x - a0) / (a1 - a0);
            return Color.FromArgb((int)(c0.R + (c1.R - c0.R) * k), (int)(c0.G + (c1.G - c0.G) * k), (int)(c0.B + (c1.B - c0.B) * k));
        }
        return Stops[^1].c;
    }

    public static Color Temp(double? c) => c == null ? Muted : Heat((c.Value - 35) / 60);
    public static Color Pct(double? p) => p == null ? Muted : Heat(p.Value / 100);
    public static Color Rpm(double? r) => r is null or 0 ? Muted : Heat(r.Value / 6000);

    private static readonly string[] Families = InstalledFamilies();
    private static string[] InstalledFamilies()
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Select(f => f.Name).ToArray();
    }

    /// <summary>Bahnschrift ships with Windows 10+ and has condensed widths, close to the app's Archivo numerals.</summary>
    public static Font Font(float px, bool bold = false, bool condensed = true)
    {
        string[] wanted = bold
            ? condensed ? new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift SemiBold" } : new[] { "Bahnschrift SemiBold" }
            : condensed ? new[] { "Bahnschrift SemiCondensed", "Bahnschrift" } : new[] { "Bahnschrift" };
        var name = wanted.FirstOrDefault(Families.Contains) ?? "Segoe UI";
        return new Font(name, px, bold && name == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public static string ShortGpu(string name, string cpuName)
    {
        if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) || name == "AMD Radeon Graphics")
        {
            // AMD reports every APU as "Radeon Graphics"; the CPU name carries the real model (e.g. "w/ Radeon 780M Graphics").
            var m = System.Text.RegularExpressions.Regex.Match(cpuName, @"Radeon\s+(\d{3,4}M)");
            return m.Success ? $"Radeon {m.Groups[1].Value}" : "Radeon";
        }
        return name.Replace("NVIDIA GeForce ", "").Replace(" Laptop GPU", "").Replace("(TM)", "").Replace("(R)", "")
            .Replace("Intel ", "").Replace("AMD ", "").Trim();
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>Borderless window drawn with per-pixel alpha through UpdateLayeredWindow.</summary>
internal abstract class LayeredWindow : Form
{
    private readonly bool _clickThrough;

    protected LayeredWindow(bool clickThrough)
    {
        _clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /* LAYERED */ | 0x00000080 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */;
            if (_clickThrough) cp.ExStyle |= 0x00000020 /* TRANSPARENT */;
            return cp;
        }
    }

    protected float UiScale => DeviceDpi / 96f;

    protected void Present(Size size, Action<Graphics> draw)
    {
        if (size.Width <= 0 || size.Height <= 0 || !IsHandleCreated) return;
        using var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            draw(g);
        }
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hbmp);
        try
        {
            var pos = new POINT { X = Left, Y = Top };
            var sz = new SIZE { cx = size.Width, cy = size.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screen, ref pos, ref sz, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            Size = size;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Re-apply "always on top" only if something removed it. Windows keeps a topmost window above normal ones, and
    /// reshuffling the z-order every second can get in the way of other apps coming to the front (e.g. from a notification).
    /// </summary>
    public void KeepOnTop()
    {
        if ((GetWindowLong(Handle, -20 /* GWL_EXSTYLE */) & 0x8 /* WS_EX_TOPMOST */) != 0) return;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] protected static extern IntPtr GetForegroundWindow();
}

/// <summary>Click-through readout drawn over games. Toggled from settings or the global hotkey.</summary>
internal sealed class OverlayWindow : LayeredWindow
{
    private record Seg(string Text, Color Color, string Unit = "");

    public OverlayWindow() : base(clickThrough: true) => Text = "Thermal overlay";

    public void Update(Snapshot s, Settings settings)
    {
        var k = UiScale * (float)Math.Clamp(settings.OverlayScale, 0.75, 2.0);
        using var label = Look.Font(11 * k, bold: true);
        using var value = Look.Font(16 * k, bold: true);
        using var unit = Look.Font(11 * k);
        var rows = Rows(s, settings.OverlayItems);
        if (rows.Count == 0) { Hide(); return; }

        var fmt = StringFormat.GenericTypographic;
        float pad = 10 * k, labelW = 40 * k, gap = 9 * k, rowH = 22 * k;
        using var measure = Graphics.FromHwnd(IntPtr.Zero);
        float SegW(Seg seg) => measure.MeasureString(seg.Text, value, PointF.Empty, fmt).Width
                               + (seg.Unit == "" ? 0 : 2 * k + measure.MeasureString(seg.Unit, unit, PointF.Empty, fmt).Width);
        var width = rows.Max(r => labelW + r.segs.Sum(SegW) + gap * (r.segs.Count - 1));
        var size = new Size((int)Math.Ceiling(width + pad * 2), (int)Math.Ceiling(rows.Count * rowH + pad * 2 - 4 * k));

        var area = Screen.FromHandle(GetForegroundWindow()).Bounds;
        var margin = (int)(14 * k);
        var x = settings.OverlayCorner.EndsWith("right") ? area.Right - size.Width - margin : area.Left + margin;
        var y = settings.OverlayCorner.StartsWith("bottom") ? area.Bottom - size.Height - margin : area.Top + margin;
        Location = new Point(x, y);

        Present(size, g =>
        {
            using (var path = Look.Rounded(new RectangleF(0, 0, size.Width - 1, size.Height - 1), 8 * k))
            using (var bg = new SolidBrush(Look.Panel))
                g.FillPath(bg, path);
            var top = pad - 2 * k;
            foreach (var (name, segs) in rows)
            {
                using (var b = new SolidBrush(Look.Muted)) g.DrawString(name, label, b, pad, top + 5 * k, fmt);
                var cx = pad + labelW;
                foreach (var seg in segs)
                {
                    using var vb = new SolidBrush(seg.Color);
                    g.DrawString(seg.Text, value, vb, cx, top, fmt);
                    cx += measure.MeasureString(seg.Text, value, PointF.Empty, fmt).Width;
                    if (seg.Unit != "")
                    {
                        using var ub = new SolidBrush(Look.Muted);
                        g.DrawString(seg.Unit, unit, ub, cx + 2 * k, top + 5 * k, fmt);
                        cx += 2 * k + measure.MeasureString(seg.Unit, unit, PointF.Empty, fmt).Width;
                    }
                    cx += gap;
                }
                top += rowH;
            }
        });
        if (!Visible) Show();
        KeepOnTop();
    }

    private static List<(string name, List<Seg> segs)> Rows(Snapshot s, string[] items)
    {
        var rows = new List<(string, List<Seg>)>();
        var dgpu = s.Gpus.FirstOrDefault(g => g.Kind == "discrete");
        var igpu = s.Gpus.FirstOrDefault(g => g.Kind == "integrated");
        foreach (var item in items)
        {
            switch (item)
            {
                case "fps" when s.Fps != null:
                    var f = s.Fps;
                    var fps = new List<Seg> { new($"{f.Fps:0}", Look.Text, "fps") };
                    if (f.Low1 != null) fps.Add(new($"{f.Low1:0}", Look.Muted, "1% low"));
                    fps.Add(new($"{f.FrameTimeMs:0.0}", Look.Muted, "ms"));
                    rows.Add(("FPS", fps));
                    break;
                case "cpu":
                    var cpu = new List<Seg> { new(Deg(s.Cpu.Temp), Look.Temp(s.Cpu.Temp), "°C"), new($"{s.Cpu.Load:0}", Look.Pct(s.Cpu.Load), "%") };
                    if (s.Cpu.ClockAvg != null) cpu.Add(new($"{s.Cpu.ClockAvg / 1000:0.00}", Look.Text, "GHz"));
                    if (s.Cpu.Power != null) cpu.Add(new($"{s.Cpu.Power:0}", Look.Text, "W"));
                    rows.Add(("CPU", cpu));
                    break;
                case "gpu" when dgpu != null:
                    rows.Add(("GPU", GpuSegs(dgpu)));
                    break;
                case "gpu" when igpu != null:
                case "igpu" when igpu != null && dgpu != null:
                    rows.Add((dgpu == null ? "GPU" : "iGPU", GpuSegs(igpu)));
                    break;
                case "vram" when (dgpu is { Active: true } ? dgpu : dgpu == null ? igpu : null) is { MemUsed: not null, MemTotal: > 0 } vg:
                    rows.Add(("VRAM", new List<Seg> { new($"{vg.MemUsed / 1073741824.0:0.0}", Look.Pct(100 * vg.MemUsed / vg.MemTotal), $"/ {vg.MemTotal / 1073741824.0:0} GB") }));
                    break;
                case "fans" when s.Fans.Length > 0:
                    rows.Add(("FAN", s.Fans.Take(3).Select(fan =>
                        fan.Rpm is null or 0 ? new Seg("off", Look.Muted) : new Seg($"{fan.Rpm:#,0}", Look.Rpm(fan.Rpm), "rpm")).ToList()));
                    break;
                case "ram":
                    rows.Add(("RAM", new List<Seg> { new($"{s.Memory.Used / 1073741824.0:0.0}", Look.Pct(100 * s.Memory.Used / s.Memory.Total), $"/ {s.Memory.Total / 1073741824.0:0} GB") }));
                    break;
                case "battery" when s.Battery is { OnAc: false } bat:
                    var b = new List<Seg> { new($"{bat.Charge:0}", Look.Text, "%") };
                    if (bat.RateW != null) b.Add(new($"{Math.Abs(bat.RateW.Value):0}", Look.Text, "W"));
                    rows.Add(("BAT", b));
                    break;
            }
        }
        return rows;
    }

    private static List<Seg> GpuSegs(GpuInfo g)
    {
        if (g.Kind == "discrete" && !g.Active) return new List<Seg> { new("asleep", Look.Muted) };
        var segs = new List<Seg>();
        if (g.Temp != null) segs.Add(new(Deg(g.Temp), Look.Temp(g.Temp), "°C"));
        segs.Add(new($"{g.Load:0}", Look.Pct(g.Load), "%"));
        if (g.ClockCore != null) segs.Add(new($"{g.ClockCore / 1000:0.00}", Look.Text, "GHz"));
        if (g.Power != null) segs.Add(new($"{g.Power:0}", Look.Text, "W"));
        return segs;
    }

    private static string Deg(double? t) => t == null ? "–" : $"{t:0}";
}

/// <summary>Movable desktop widget with temperatures, loads, fans, memory and two-minute temperature traces.</summary>
internal sealed class WidgetWindow : LayeredWindow
{
    private readonly Queue<(double? cpu, double? gpu)> _history = new();
    private readonly Settings _settings;
    private readonly Action _open;
    private readonly Action _hide;
    private Snapshot? _last;

    public WidgetWindow(Settings settings, Action open, Action hide) : base(clickThrough: false)
    {
        _settings = settings;
        _open = open;
        _hide = hide;
        Text = "Thermal widget";
        TopMost = settings.WidgetOnTop;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Thermal", null, (_, _) => _open());
        var onTop = new ToolStripMenuItem("Keep on top") { Checked = settings.WidgetOnTop, CheckOnClick = true };
        onTop.CheckedChanged += (_, _) => { _settings.WidgetOnTop = onTop.Checked; TopMost = onTop.Checked; _settings.Save(); };
        menu.Items.Add(onTop);
        menu.Items.Add("Hide widget", null, (_, _) => _hide());
        ContextMenuStrip = menu;
        MouseDoubleClick += (_, _) => _open();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_EXITSIZEMOVE = 0x232, WM_NCRBUTTONUP = 0xA5, WM_NCLBUTTONDBLCLK = 0xA3;
        if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(2 /* HTCAPTION: drag anywhere */); return; }
        if (m.Msg == WM_NCRBUTTONUP) { ContextMenuStrip?.Show(Cursor.Position); return; }
        if (m.Msg == WM_NCLBUTTONDBLCLK) { _open(); return; }
        if (m.Msg == WM_EXITSIZEMOVE) { _settings.WidgetX = Left; _settings.WidgetY = Top; _settings.Save(); }
        base.WndProc(ref m);
    }

    public void Place()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var p = new Point(_settings.WidgetX, _settings.WidgetY);
        Location = _settings.WidgetX >= 0 && Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(p))
            ? p
            : new Point(area.Right - (int)(296 * UiScale) - 24, area.Top + 24);
    }

    public void Update(Snapshot s)
    {
        _last = s;
        var dgpu = s.Gpus.FirstOrDefault(g => g.Kind == "discrete") ?? s.Gpus.FirstOrDefault();
        _history.Enqueue((s.Cpu.Temp, dgpu?.Temp));
        while (_history.Count > 120) _history.Dequeue();
        if (!Visible) return;
        Draw();
        if (_settings.WidgetOnTop) KeepOnTop();
    }

    public void Draw()
    {
        var s = _last;
        if (s == null) return;
        var k = UiScale;
        var size = new Size((int)(296 * k), (int)(232 * k));
        var fmt = StringFormat.GenericTypographic;
        Present(size, g =>
        {
            using (var path = Look.Rounded(new RectangleF(0, 0, size.Width - 1, size.Height - 1), 12 * k))
            using (var bg = new SolidBrush(Color.FromArgb(255, 29, 36, 51)))
                g.FillPath(bg, path);

            using var small = Look.Font(11 * k);
            using var smallBold = Look.Font(11 * k, bold: true);
            using var big = Look.Font(30 * k, condensed: true);
            using var mid = Look.Font(13 * k, bold: true);
            using var muted = new SolidBrush(Look.Muted);
            using var text = new SolidBrush(Look.Text);
            float pad = 14 * k;

            // Three temperature columns: CPU and each GPU.
            var cols = new List<(string name, double? temp, double? load, bool asleep)> { ("CPU", s.Cpu.Temp, s.Cpu.Load, false) };
            cols.AddRange(s.Gpus.Select(gp => (Look.ShortGpu(gp.Name, s.Cpu.Name), gp.Temp, gp.Load, gp.Kind == "discrete" && !gp.Active)));
            var colW = (size.Width - pad * 2) / cols.Count;
            for (var i = 0; i < cols.Count; i++)
            {
                var (name, temp, load, asleep) = cols[i];
                var x = pad + i * colW;
                g.DrawString(name, small, muted, x, pad, fmt);
                // A sleeping dedicated GPU says so; a GPU without a temperature reading shows its load as the big number.
                var (big1, color, sub) = asleep ? ("off", Look.Faint, "asleep")
                    : temp != null ? ($"{temp:0}°", Look.Temp(temp), $"{load:0}% load")
                    : ($"{load:0}%", Look.Pct(load), "load");
                using var tb = new SolidBrush(color);
                g.DrawString(big1, big, tb, x - 1 * k, pad + 14 * k, fmt);
                using var lb = new SolidBrush(asleep ? Look.Faint : Look.Pct(load));
                g.DrawString(sub, small, lb, x, pad + 50 * k, fmt);
            }

            // Two-minute traces: CPU solid, GPU dimmer.
            var chart = new RectangleF(pad, pad + 72 * k, size.Width - pad * 2, 44 * k);
            using (var track = new SolidBrush(Color.FromArgb(120, Look.Track))) g.FillRectangle(track, chart);
            var pts = _history.ToArray();
            DrawTrace(g, chart, pts.Select(p => p.cpu).ToArray(), 255, k);
            DrawTrace(g, chart, pts.Select(p => p.gpu).ToArray(), 120, k);

            // Fans, memory, frame rate.
            var y = chart.Bottom + 12 * k;
            var fans = s.Fans.Length == 0 ? "No fan sensors" : string.Join("    ", s.Fans.Take(2).Select(f => $"{f.Name.Replace(" fan", "")} {(f.Rpm is null or 0 ? "off" : $"{f.Rpm:#,0} rpm")}"));
            g.DrawString("Fans", smallBold, muted, pad, y, fmt);
            g.DrawString(fans, small, text, pad + 44 * k, y, fmt);
            y += 20 * k;
            g.DrawString("Memory", smallBold, muted, pad, y, fmt);
            var memPct = s.Memory.Used / s.Memory.Total;
            g.DrawString($"{s.Memory.Used / 1073741824.0:0.0} of {s.Memory.Total / 1073741824.0:0} GB", small, text, pad + 44 * k, y, fmt);
            var bar = new RectangleF(pad + 160 * k, y + 4 * k, size.Width - pad * 2 - 160 * k, 5 * k);
            using (var tr = new SolidBrush(Look.Track)) g.FillRectangle(tr, bar);
            using (var fill = new SolidBrush(Look.Heat(memPct))) g.FillRectangle(fill, bar.X, bar.Y, (float)(bar.Width * memPct), bar.Height);
            y += 20 * k;
            g.DrawString(s.Fps != null ? "Game" : "Power", smallBold, muted, pad, y, fmt);
            var last = s.Fps != null
                ? $"{s.Fps.Fps:0} fps{(s.Fps.Low1 != null ? $", 1% low {s.Fps.Low1:0}" : "")}  {s.Fps.Name}"
                : s.Battery is { } b ? $"{(b.OnAc ? "Plugged in" : "Battery")} {b.Charge:0}%{(b.RateW is double w && w != 0 ? $", {Math.Abs(w):0} W" : "")}" : "Plugged in";
            g.DrawString(last, small, text, pad + 44 * k, y, fmt);
        });
    }

    private static void DrawTrace(Graphics g, RectangleF r, double?[] vals, int alpha, float k)
    {
        if (vals.Length < 2) return;
        var step = r.Width / 119f;
        var x0 = r.Right - (vals.Length - 1) * step;
        for (var i = 1; i < vals.Length; i++)
        {
            if (vals[i - 1] is not double a || vals[i] is not double b) continue;
            float Y(double v) => r.Bottom - (float)Math.Clamp((v - 30) / 70, 0, 1) * r.Height;
            using var pen = new Pen(Color.FromArgb(alpha, Look.Temp(b)), 1.6f * k);
            g.DrawLine(pen, x0 + (i - 1) * step, Y(a), x0 + i * step, Y(b));
        }
    }
}
