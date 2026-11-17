using System.Text.Json;

namespace PhoneMirror;

/// <summary>A phone Phone Mirror has seen before, keyed by its hardware serial.</summary>
class KnownPhone
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";       // nickname; empty = use model
    public string Model { get; set; } = "";
    public string LastIp { get; set; } = "";     // reachable on :5555 last time
    public DateTime LastSeen { get; set; }
    public string Look { get; set; } = "";       // "" = use the default look
    public int? Quality { get; set; }            // null = use the default quality
    public int WindowSize { get; set; }          // long side of the screen in px; 0 = automatic
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool NoH265 { get; set; }
    public bool NoHighProfile { get; set; }      // encoder rejects the H.264 High profile             // phone can't encode H.265: use H.264 over Wi-Fi

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Model : Name;
}

class Settings
{
    public string LastIp { get; set; } = "";
    /// <summary>-1 = Auto (picks per USB / Wi-Fi), 0 = full resolution, else max size in pixels.</summary>
    public int MaxSize { get; set; } = -1;
    /// <summary>0 = Auto (match the display), otherwise frames per second.</summary>
    public int FrameRate { get; set; }
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

    public List<KnownPhone> Phones { get; set; } = new();

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
