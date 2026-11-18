using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PhoneMirror;

/// <summary>
/// Serves the player page on 127.0.0.1 so it runs as a secure page (needed for hardware video
/// decoding) that may still talk to the phone over the local Wi-Fi.
/// </summary>
static class LocalWeb
{
    static TcpListener? listener;
    static byte[]? page;
    public static int Port { get; private set; }

    public static void Start()
    {
        if (listener != null) return;
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mirror.html")!)
        using (var ms = new MemoryStream()) { s.CopyTo(ms); page = ms.ToArray(); }
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Loop);
    }

    static async Task Loop()
    {
        while (listener != null)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(); } catch { break; }
            _ = Task.Run(async () =>
            {
                using (c)
                {
                    try
                    {
                        var stream = c.GetStream();
                        var buf = new byte[4096];
                        await stream.ReadAsync(buf);  // one small GET; the path doesn't matter
                        var head = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n" +
                            $"Content-Length: {page!.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head);
                        await stream.WriteAsync(page);
                    }
                    catch { }
                }
            });
        }
    }
}

/// <summary>
/// A borderless window holding the player page. It starts off-screen; the phone frame
/// places it over its screen area once the phone's picture size is known.
/// </summary>
class ScreenHostForm : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly string url;
    readonly bool plain;
    bool ready, placed;

    public bool IsReady => ready && IsHandleCreated;
    public bool IsFinished { get; private set; }
    public Action<string>? Log;
    public Action? Connected;

    /// <param name="plain">Show as an ordinary resizable window instead of inside the phone frame.</param>
    public ScreenHostForm(string url, bool plain = false, string title = "Phone Mirror", bool topMost = false)
    {
        this.url = url;
        this.plain = plain;
        Text = title + " — Phone Mirror";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        TopMost = plain && topMost;
        FormBorderStyle = plain ? FormBorderStyle.Sizable : FormBorderStyle.None;
        ShowInTaskbar = plain;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-20000, -20000);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(360, 780);
        BackColor = Color.Black;
        Controls.Add(web);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            await web.EnsureCoreWebView2Async(MainForm.WebEnv);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.PermissionRequested += (_, a) =>
            {
                if (a.PermissionKind == CoreWebView2PermissionKind.ClipboardRead) a.State = CoreWebView2PermissionState.Allow;
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            Log?.Invoke("Couldn't start the player: " + ex.Message);
            Close();
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetString())
            {
                case "size":
                    int w = m.GetProperty("w").GetInt32(), h = m.GetProperty("h").GetInt32();
                    if (w <= 0 || h <= 0) break;
                    // Keep the same long side, change the shape (e.g. the phone rotated).
                    int longSide = Math.Max(ClientSize.Width, ClientSize.Height);
                    ClientSize = w < h
                        ? new Size((int)Math.Round(longSide * (double)w / h), longSide)
                        : new Size(longSide, (int)Math.Round(longSide * (double)h / w));
                    ready = true;
                    PlacePlain();
                    break;
                case "needCode":
                    ready = true;   // show the phone so the code can be typed
                    PlacePlain();
                    break;
                case "ok":
                    Connected?.Invoke();
                    ready = true;
                    PlacePlain();
                    break;
                case "error":
                    Log?.Invoke(m.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "Error");
                    break;
                case "log":
                    Log?.Invoke(m.TryGetProperty("text", out var lt) ? lt.GetString() ?? "" : "");
                    break;
                case "closed":
                    Close();
                    break;
            }
        }
        catch { }
    }

    /// <summary>Plain window: bring it on screen the first time, sized like the phone.</summary>
    void PlacePlain()
    {
        if (!plain || placed) return;
        placed = true;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 728);
        double aspect = (double)ClientSize.Width / Math.Max(1, ClientSize.Height);
        int h = (int)(wa.Height * 0.85);
        int w = (int)Math.Round(h * aspect);
        if (aspect > 1) { w = (int)(wa.Width * 0.7); h = (int)Math.Round(w / aspect); }
        ClientSize = new Size(w, h);
        Location = new Point(wa.Right - Width - 60, wa.Top + (wa.Height - Height) / 2);
        Activate();
    }

    public void SetQuality(int maxSize, int bitrate)
    {
        try { _ = web.CoreWebView2?.ExecuteScriptAsync($"pm.quality({maxSize},{bitrate})"); } catch { }
    }

    public void Key(string k)
    {
        try { _ = web.CoreWebView2?.ExecuteScriptAsync($"pm.key({JsonSerializer.Serialize(k)})"); } catch { }
    }

    public async Task<string?> ScreenshotDataUrl()
    {
        try
        {
            if (web.CoreWebView2 == null) return null;
            var json = await web.CoreWebView2.ExecuteScriptAsync("pm.shot()");
            return JsonSerializer.Deserialize<string>(json);
        }
        catch { return null; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        IsFinished = true;
        try { _ = web.CoreWebView2?.ExecuteScriptAsync("pm.close()"); } catch { }
        base.OnFormClosing(e);
    }
}

/// <summary>Mirror through the Phone Mirror app on the phone.</summary>
class AppSource : IMirrorSource
{
    readonly ScreenHostForm host;
    readonly string shotDir;
    readonly Action<string> log;

    public AppSource(ScreenHostForm host, string name, string shotDir, Action<string> log)
    {
        this.host = host;
        this.shotDir = shotDir;
        this.log = log;
        Name = name;
    }

    public string Name { get; }
    public bool HasExited => host.IsFinished || host.IsDisposed;
    public IntPtr FindWindow() => host.IsReady ? host.Handle : IntPtr.Zero;
    public void Key(string key) => host.Key(key);
    public void Kill() { try { if (!host.IsDisposed) host.Close(); } catch { } }

    public async Task Screenshot()
    {
        var data = await host.ScreenshotDataUrl();
        const string prefix = "data:image/png;base64,";
        if (string.IsNullOrEmpty(data) || !data.StartsWith(prefix)) { log("Screenshot isn't ready yet."); return; }
        Directory.CreateDirectory(shotDir);
        var safe = string.Concat(Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var file = Path.Combine(shotDir, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        await File.WriteAllBytesAsync(file, Convert.FromBase64String(data[prefix.Length..]));
        log($"Screenshot saved: {file}");
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }
}
