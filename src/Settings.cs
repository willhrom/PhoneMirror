using System.Text.Json;

namespace PhoneMirror;

class Settings
{
    public string LastIp { get; set; } = "";
    /// <summary>-1 = Auto (picks per USB / Wi-Fi), 0 = full resolution, else max size in pixels.</summary>
    public int MaxSize { get; set; } = -1;
    public bool Audio { get; set; } = true;
    public bool ScreenOff { get; set; }
    public bool StayAwake { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool Fullscreen { get; set; }
    public bool Record { get; set; }
    /// <summary>Phone look: Black, Silver, Blue, or Off (plain window).</summary>
    public string FrameStyle { get; set; } = "Black";
    /// <summary>Phone buttons beside the frame: hover, always, or off.</summary>
    public string ButtonStrip { get; set; } = "hover";
    public bool AutoMirror { get; set; } = true;
    public bool AutoWifi { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool InstallAsked { get; set; }
    public bool TrayTipShown { get; set; }

    static string FilePath => Path.Combine(Tools.AppDir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Tools.AppDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
