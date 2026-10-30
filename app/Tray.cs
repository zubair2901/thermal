using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Thermal;

/// <summary>Notification-area icon. Shows the CPU temperature as a number when that setting is on.</summary>
internal sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private IntPtr _drawn;
    private string _lastText = "";

    public ToolStripMenuItem Overlay { get; } = new("Show overlay") { CheckOnClick = true };
    public ToolStripMenuItem Widget { get; } = new("Show widget") { CheckOnClick = true };
    public ToolStripMenuItem Record { get; } = new("Start recording");

    public Tray(Icon appIcon, Action open, Action exit)
    {
        _appIcon = appIcon;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Thermal", null, (_, _) => open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Overlay);
        menu.Items.Add(Widget);
        menu.Items.Add(Record);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon { Icon = appIcon, Text = "Thermal", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) open(); };
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Warning);
    public void Info(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    public void Update(double? cpuTemp, string tooltip, bool showTemp)
    {
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var text = showTemp && cpuTemp != null ? $"{cpuTemp:0}" : "";
        if (text == _lastText) return;
        _lastText = text;
        if (text == "")
        {
            _icon.Icon = _appIcon;
            Free();
            return;
        }

        var size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var font = Look.Font(size * (text.Length > 2 ? 0.62f : 0.82f), bold: true);
            using var brush = new SolidBrush(Look.Temp(cpuTemp));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(-1, 0, size + 2, size), sf);
        }
        var h = bmp.GetHicon();
        _icon.Icon = Icon.FromHandle(h);
        Free();
        _drawn = h;
    }

    private void Free()
    {
        if (_drawn != IntPtr.Zero) DestroyIcon(_drawn);
        _drawn = IntPtr.Zero;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        Free();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
}
