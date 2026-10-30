using System.Text.Json;

namespace Thermal;

/// <summary>User preferences, saved to %APPDATA%\Thermal\settings.json. The UI edits them; the host applies them.</summary>
public sealed class Settings
{
    public bool PollDgpu { get; set; } = true;

    public bool OverlayEnabled { get; set; }
    public string OverlayHotkey { get; set; } = "Ctrl+Shift+O";
    public string OverlayCorner { get; set; } = "top-left";
    public double OverlayScale { get; set; } = 1.0;
    public string[] OverlayItems { get; set; } = { "fps", "cpu", "gpu", "vram", "fans", "ram" };

    public bool WidgetEnabled { get; set; }
    public bool WidgetOnTop { get; set; } = true;
    public int WidgetX { get; set; } = -1;
    public int WidgetY { get; set; } = -1;

    public bool AlertsEnabled { get; set; } = true;
    public double CpuTempAlert { get; set; } = 95;
    public double GpuTempAlert { get; set; } = 87;
    public int AlertSeconds { get; set; } = 30;

    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool TrayTemperature { get; set; } = true;

    public int[]? WindowBounds { get; set; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thermal");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception e) { MainForm.Log(e); }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { MainForm.Log(e); }
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json), Json)!;
}
