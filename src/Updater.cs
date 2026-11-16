using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace PhoneMirror;

/// <summary>Checks GitHub Releases for a newer PhoneMirror.exe and swaps it in.</summary>
static class Updater
{
    const string Api = "https://api.github.com/repos/willhrom/PhoneMirror/releases/latest";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static async Task<(Version Version, string Url)?> CheckAsync()
    {
        using var http = Tools.NewHttp();
        var json = await http.GetStringAsync(Api);
        using var doc = JsonDocument.Parse(json);
        var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var v)) return null;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        if (v <= Current) return null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (string.Equals(a.GetProperty("name").GetString(), "PhoneMirror.exe", StringComparison.OrdinalIgnoreCase))
                return (v, a.GetProperty("browser_download_url").GetString()!);
        return null;
    }

    /// <summary>Downloads the new exe, swaps it with the running one and starts it.</summary>
    public static async Task ApplyAsync(string url, Action<int> progress)
    {
        var exe = Environment.ProcessPath ?? throw new Exception("Can't find the running app.");
        var tmp = exe + ".new";
        var old = exe + ".old";
        await Tools.DownloadAsync(url, tmp, progress);
        if (new FileInfo(tmp).Length < 1_000_000) throw new Exception("The download looks incomplete.");
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);     // Windows allows renaming a running exe
        File.Move(tmp, exe);
        Process.Start(new ProcessStartInfo(exe, "--wait") { UseShellExecute = false });
    }

    public static void Cleanup()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            foreach (var f in new[] { exe + ".old", exe + ".new" })
                if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }
}
