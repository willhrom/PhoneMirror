using System.Text.Json;

namespace PhoneMirror;

class Settings
{
    public string LastIp { get; set; } = "";
    public int MaxSize { get; set; } = 1920;
    public bool Audio { get; set; } = true;
    public bool ScreenOff { get; set; }
    public bool StayAwake { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool Fullscreen { get; set; }
    public bool Record { get; set; }

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
