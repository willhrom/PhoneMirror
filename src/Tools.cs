using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace PhoneMirror;

record Device(string Serial, string State, string Model)
{
    public bool Wireless => Serial.Contains(':') || Serial.Contains("_adb-tls-connect");
    public bool Ready => State == "device";
    public string Connection => Wireless ? "Wi-Fi" : "USB";
}

/// <summary>Finds, installs and runs scrcpy + adb (the mirroring engine).</summary>
static class Tools
{
    public static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneMirror");
    static string ToolDir => Path.Combine(AppDir, "scrcpy");
    static string VersionFile => Path.Combine(AppDir, "scrcpy-version.txt");

    public static string? Scrcpy { get; private set; }
    public static string? Adb { get; private set; }
    public static string InstalledVersion => File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : "?";

    static readonly string[] KnownStates = { "device", "unauthorized", "offline", "authorizing", "connecting", "recovery", "sideload" };

    public static bool Locate()
    {
        if (!Directory.Exists(ToolDir)) return false;
        Scrcpy = Directory.EnumerateFiles(ToolDir, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault();
        Adb = Directory.EnumerateFiles(ToolDir, "adb.exe", SearchOption.AllDirectories).FirstOrDefault();
        return Scrcpy != null && Adb != null;
    }

    /// <summary>Downloads the latest scrcpy for Windows from GitHub.</summary>
    public static async Task<string> InstallAsync(Action<string, int> progress)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneMirror");

        progress("Checking for the latest scrcpy…", 0);
        var json = await http.GetStringAsync("https://api.github.com/repos/Genymobile/scrcpy/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "?";

        string? url = null, name = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            if (n.StartsWith("scrcpy-win64-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                url = a.GetProperty("browser_download_url").GetString();
                name = n;
                break;
            }
        }
        if (url == null || name == null) throw new Exception("Couldn't find the Windows download in the latest scrcpy release.");

        var tmp = Path.Combine(Path.GetTempPath(), name);
        await DownloadAsync(url, tmp, pct => progress($"Downloading scrcpy {tag}… {pct}%", pct));

        progress("Unpacking…", 100);
        if (Adb != null) await RunAsync(Adb, new[] { "kill-server" }, 5000);
        await Task.Run(() =>
        {
            if (Directory.Exists(ToolDir)) Directory.Delete(ToolDir, true);
            Directory.CreateDirectory(AppDir);
            ZipFile.ExtractToDirectory(tmp, ToolDir, true);
            File.Delete(tmp);
            File.WriteAllText(VersionFile, tag);
        });
        if (!Locate()) throw new Exception("scrcpy downloaded but its files weren't found.");
        return tag;
    }

    public static HttpClient NewHttp()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PhoneMirror");
        return http;
    }

    public static async Task DownloadAsync(string url, string path, Action<int> progress)
    {
        using var http = NewHttp();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0, done = 0;
        int lastPct = -1;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        int r;
        while ((r = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, r));
            done += r;
            int pct = total > 0 ? (int)(done * 100 / total) : 0;
            if (pct != lastPct) { lastPct = pct; progress(pct); }
        }
    }

    /// <summary>Start info for a hidden adb process (mDNS enabled for wireless pairing).</summary>
    public static ProcessStartInfo AdbStartInfo(params string[] args)
    {
        var psi = new ProcessStartInfo(Adb!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(Adb) ?? "",
        };
        psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Runs a command hidden and returns its combined output.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "Timed out");
        }
        // adb's background server can hold the pipes open, so don't wait forever on them.
        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1500));
        var text = (outTask.IsCompletedSuccessfully ? outTask.Result : "") + "\n" +
                   (errTask.IsCompletedSuccessfully ? errTask.Result : "");
        return (p.ExitCode, text.Trim());
    }

    public static Task<(int Code, string Output)> AdbAsync(params string[] args) => RunAsync(Adb!, args);
    public static Task<(int Code, string Output)> AdbAsync(int timeoutMs, params string[] args) => RunAsync(Adb!, args, timeoutMs);

    public static async Task<List<Device>> DevicesAsync()
    {
        var (_, output) = await AdbAsync(8000, "devices", "-l");
        var list = new List<Device>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !KnownStates.Contains(parts[1])) continue;
            var model = parts.FirstOrDefault(x => x.StartsWith("model:"))?.Substring(6).Replace('_', ' ') ?? "Android phone";
            list.Add(new Device(parts[0], parts[1], model));
        }
        return list;
    }

    /// <summary>Saves a PNG screenshot of the phone.</summary>
    public static async Task<bool> ScreenshotAsync(string serial, string file)
    {
        var psi = AdbStartInfo("-s", serial, "exec-out", "screencap", "-p");
        using var p = Process.Start(psi)!;
        await using (var fs = File.Create(file))
            await p.StandardOutput.BaseStream.CopyToAsync(fs);
        using var cts = new CancellationTokenSource(15000);
        try { await p.WaitForExitAsync(cts.Token); } catch { try { p.Kill(true); } catch { } }
        return new FileInfo(file).Length > 1000;
    }
}
