using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using QRCoder;

namespace PhoneMirror;

public class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(11, 13, 18);
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly Regex AddrRx = new(@"\d+\.\d+\.\d+\.\d+:\d+");

    readonly Settings cfg = Settings.Load();
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    readonly HashSet<string> wifiDone = new();
    readonly HashSet<string> mirroring = new();
    readonly List<(string Time, string Text, string Level)> logHistory = new();

    List<Device> devices = new();
    string deviceSignature = "";
    bool uiReady, refreshing, refreshAgain, engineReady, allowAuto, closing, reallyQuit;
    int mirrorsOpen;
    DateTime lastReconnect = DateTime.MinValue;
    string? updateUrl, updateVersion, busyText;
    int busyPct;
    Process? tracker;
    CancellationTokenSource? qrCts;

    static string RecordDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Phone Mirror");
    static string ShotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Phone Mirror");

    public MainForm()
    {
        var v = Updater.Current;
        Text = "Phone Mirror";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Bg;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1060, 720);
        MinimumSize = new Size(880, 600);
        StartPosition = FormStartPosition.CenterScreen;
        if (Program.Background) Opacity = 0;
        Controls.Add(web);

        tray.Icon = Icon;
        tray.Text = "Phone Mirror";
        tray.Visible = true;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Phone Mirror", null, (_, _) => ShowWindow());
        trayMenu.Items.Add("Mirror my phone", null, (_, _) => StartMirror(null));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Quit", null, (_, _) => { reallyQuit = true; Close(); });
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowWindow();

        if (Program.ShowSignal != null)
            ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal, (_, _) => Ui(ShowWindow), null, -1, false);

        poll.Tick += async (_, _) => { await RefreshDevices(); await ReconnectWifi(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1, caption = 0x00120D0B; // BGR of the app background
            DwmSetWindowAttribute(Handle, 20, ref on, 4);       // dark title bar
            DwmSetWindowAttribute(Handle, 35, ref caption, 4);  // title bar color (Windows 11)
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    // ================= startup / shutdown =================

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Program.Background) { Hide(); Opacity = 1; }
        if (OfferInstall()) return;
        if (cfg.StartWithWindows) Installer.SetStartWithWindows(true);

        if (!await InitWebView()) return;

        try
        {
            if (!Tools.Locate())
            {
                Log("First run: downloading the mirroring engine (scrcpy, free and open source)…");
                await InstallEngine();
            }
            else Log($"Mirroring engine ready (scrcpy {Tools.InstalledVersion}).");

            await Tools.AdbAsync(15000, "start-server");
            engineReady = true;
            PushState();

            allowAuto = !Program.Background;
            await RefreshDevices();
            allowAuto = true;
            _ = Task.Run(TrackDevicesLoop);
            poll.Start();
            if (devices.Count == 0) Log("Waiting for a phone. Plug it in with a USB cable.");
            _ = ReconnectWifi();
        }
        catch (Exception ex)
        {
            Log("Setup failed: " + ex.Message, "error");
            Log("Check your internet connection, then click \"Update engine\" below.", "warn");
        }
        _ = CheckForUpdate();
    }

    async Task<bool> InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Tools.AppDir, "WebView2"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (_, a) => { a.Handled = true; OpenUrl(a.Uri); };
            core.NavigationStarting += (_, a) =>
            {
                if (a.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenUrl(a.Uri); }
            };
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui.html")!;
            using var reader = new StreamReader(s);
            core.NavigateToString(await reader.ReadToEndAsync());
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var r = MessageBox.Show(this,
                "Phone Mirror needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.\n\nOpen the download page now?",
                "Phone Mirror", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes) OpenUrl("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            reallyQuit = true;
            Close();
            return false;
        }
    }

    bool OfferInstall()
    {
        if (Program.Background || Installer.IsInstalled || cfg.InstallAsked) return false;
        cfg.InstallAsked = true;
        cfg.Save();
        var r = MessageBox.Show(this,
            "Add Phone Mirror to your Start menu and desktop?\n\nThat way you can open it anytime without finding the download.",
            "Install Phone Mirror", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return false;
        try
        {
            Installer.Install();
            Process.Start(new ProcessStartInfo(Installer.Exe, "--wait") { UseShellExecute = false });
            reallyQuit = true;
            Close();
            return true;
        }
        catch (Exception ex)
        {
            Log("Couldn't install: " + ex.Message, "error");
            return false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyQuit && e.CloseReason == CloseReason.UserClosing && cfg.StartWithWindows)
        {
            e.Cancel = true;
            Hide();
            if (!cfg.TrayTipShown)
            {
                cfg.TrayTipShown = true;
                cfg.Save();
                tray.ShowBalloonTip(4000, "Phone Mirror is still running",
                    "It's in the tray, ready for when you plug in your phone. Right-click › Quit to close it.", ToolTipIcon.Info);
            }
            return;
        }
        closing = true;
        poll.Stop();
        qrCts?.Cancel();
        try { tracker?.Kill(); } catch { }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    // ================= UI bridge =================

    void Post(object message)
    {
        if (!uiReady || closing || web.CoreWebView2 == null) return;
        try { web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, J)); } catch { }
    }

    void PushState()
    {
        var v = Updater.Current;
        Post(new
        {
            type = "state",
            version = $"{v.Major}.{v.Minor}.{v.Build}",
            engine = new { ready = engineReady, version = Tools.InstalledVersion },
            devices = devices.Select(d => new
            {
                serial = d.Serial,
                model = d.Model,
                connection = d.Connection,
                state = d.State,
                ready = d.Ready,
                mirroring = mirroring.Contains(d.Serial),
            }),
            settings = cfg,
            update = updateVersion,
            busy = busyText == null ? null : new { text = busyText, pct = busyPct },
        });
    }

    void Toast(string text) => Post(new { type = "toast", text });

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            string? Str(string k) => r.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            Device? Dev() { var sn = Str("serial"); return sn == null ? null : devices.FirstOrDefault(d => d.Serial == sn); }

            switch (Str("cmd"))
            {
                case "ready":
                    uiReady = true;
                    Post(new { type = "logHistory", items = logHistory.Select(l => new { time = l.Time, text = l.Text, level = l.Level }) });
                    PushState();
                    break;
                case "mirror": StartMirror(Dev()); break;
                case "screenshot": await Screenshot(Dev()); break;
                case "enableWifi": await ManualEnableWifi(Dev()); break;
                case "disconnect": await Disconnect(Dev()); break;
                case "setting": ApplySetting(Str("key") ?? "", r.GetProperty("value")); break;
                case "connectIp": await ConnectIp(Str("ip") ?? ""); break;
                case "pairCode": await PairWithCode(Str("addr") ?? "", Str("code") ?? ""); break;
                case "qrStart": await StartQr(); break;
                case "qrCancel": qrCts?.Cancel(); break;
                case "openFolder": OpenFolder(Str("which") == "shots" ? ShotDir : RecordDir); break;
                case "updateEngine": await UpdateEngine(); break;
                case "applyUpdate": await ApplyUpdate(); break;
                case "copyLog":
                    Clipboard.SetText(string.Join("\r\n", logHistory.Select(l => $"{l.Time}  {l.Text}")) + "\r\n");
                    Toast("Activity copied");
                    break;
            }
        }
        catch (Exception ex) { Log("Error: " + ex.Message, "error"); }
    }

    void ApplySetting(string key, JsonElement value)
    {
        bool B() => value.ValueKind == JsonValueKind.True;
        switch (key)
        {
            case "autoMirror": cfg.AutoMirror = B(); break;
            case "autoWifi": cfg.AutoWifi = B(); break;
            case "startWithWindows": cfg.StartWithWindows = B(); Installer.SetStartWithWindows(cfg.StartWithWindows); break;
            case "audio": cfg.Audio = B(); break;
            case "screenOff": cfg.ScreenOff = B(); break;
            case "stayAwake": cfg.StayAwake = B(); break;
            case "alwaysOnTop": cfg.AlwaysOnTop = B(); break;
            case "fullscreen": cfg.Fullscreen = B(); break;
            case "record": cfg.Record = B(); break;
            case "maxSize": cfg.MaxSize = value.GetInt32(); break;
            case "frameStyle": cfg.FrameStyle = value.GetString() ?? "Black"; break;
            case "buttonStrip": cfg.ButtonStrip = value.GetString() ?? "hover"; break;
        }
        cfg.Save();
        PushState();
    }

    // ================= engine =================

    void SetBusy(string? text, int pct = 0)
    {
        busyText = text;
        busyPct = pct;
        PushState();
    }

    async Task InstallEngine()
    {
        try
        {
            SetBusy("Downloading the mirroring engine…", 0);
            var tag = await Tools.InstallAsync((msg, pct) => SetBusy(msg, pct));
            Log($"Installed scrcpy {tag}.", "good");
        }
        finally { SetBusy(null); }
    }

    async Task UpdateEngine()
    {
        if (mirrorsOpen > 0) { Toast("Close your phone windows first"); return; }
        poll.Stop();
        engineReady = false;
        try { tracker?.Kill(); } catch { }
        try
        {
            Log("Updating the mirroring engine…");
            await InstallEngine();
            await Tools.AdbAsync(15000, "start-server");
            engineReady = true;
            deviceSignature = "";
        }
        catch (Exception ex) { Log("Update failed: " + ex.Message, "error"); }
        PushState();
        await RefreshDevices();
        poll.Start();
    }

    // ================= app updates =================

    async Task CheckForUpdate()
    {
        try
        {
            var u = await Updater.CheckAsync();
            if (u == null) return;
            updateUrl = u.Value.Url;
            updateVersion = $"{u.Value.Version.Major}.{u.Value.Version.Minor}.{u.Value.Version.Build}";
            PushState();
        }
        catch { }
    }

    async Task ApplyUpdate()
    {
        if (updateUrl == null) return;
        try
        {
            Log("Downloading update…");
            await Updater.ApplyAsync(updateUrl, pct => SetBusy($"Updating Phone Mirror… {pct}%", pct));
            reallyQuit = true;
            Close();
        }
        catch (Exception ex)
        {
            SetBusy(null);
            Log("Update failed: " + ex.Message, "error");
        }
    }

    // ================= devices =================

    async Task TrackDevicesLoop()
    {
        while (!closing)
        {
            try
            {
                if (engineReady && Tools.Adb != null)
                {
                    using var p = Process.Start(Tools.AdbStartInfo("track-devices"))!;
                    tracker = p;
                    var reader = p.StandardOutput;
                    var head = new char[4];
                    while (!closing)
                    {
                        if (!await ReadExact(reader, head)) break;
                        int len = Convert.ToInt32(new string(head), 16);
                        if (len > 0 && !await ReadExact(reader, new char[len])) break;
                        Ui(() => _ = RefreshDevices());
                    }
                }
            }
            catch { }
            if (!closing) await Task.Delay(3000);
        }
    }

    static async Task<bool> ReadExact(StreamReader r, char[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await r.ReadAsync(buf, got, buf.Length - got);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    async Task RefreshDevices()
    {
        if (!engineReady) return;
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                await RefreshOnce();
            } while (refreshAgain && !closing);
        }
        catch (Exception ex) { Log("Couldn't check for phones: " + ex.Message, "error"); }
        finally { refreshing = false; }
    }

    async Task RefreshOnce()
    {
        var now = await Tools.DevicesAsync();
        var sig = string.Join("|", now.Select(d => d.Serial + d.State + d.Model));
        if (sig == deviceSignature) return;

        var prev = devices;
        var newlyReady = now.Where(d => d.Ready && !prev.Any(o => o.Serial == d.Serial && o.Ready)).ToList();
        foreach (var d in now.Where(d => !prev.Any(o => o.Serial == d.Serial && o.State == d.State)))
        {
            if (d.State == "unauthorized") Log($"{d.Model}: unlock the phone and tap \"Allow\" on the USB debugging prompt.", "warn");
            else if (d.Ready) Log($"{d.Model} connected over {d.Connection}.", "good");
            else if (d.State == "offline") Log($"{d.Model} is offline. Try unplugging and plugging it back in.", "warn");
        }
        foreach (var d in prev.Where(o => !now.Any(n => n.Serial == o.Serial)))
            Log($"{d.Model} ({d.Connection}) disconnected.");

        deviceSignature = sig;
        devices = now;
        PushState();

        var usb = newlyReady.FirstOrDefault(d => !d.Wireless);
        if (usb != null && allowAuto) await OnUsbPhoneArrived(usb);
    }

    async Task OnUsbPhoneArrived(Device usb)
    {
        bool wantMirror = cfg.AutoMirror && mirrorsOpen == 0;
        if (wantMirror && !Visible) tray.ShowBalloonTip(2500, "Phone Mirror", $"Opening {usb.Model}…", ToolTipIcon.None);

        var target = usb;
        if (cfg.AutoWifi && !wifiDone.Contains(usb.Serial))
        {
            // Turning on Wi-Fi mode briefly restarts the phone's debug connection,
            // so it has to happen before the mirror opens (only once per phone restart).
            bool alreadyOn = await WifiAlreadyOn(usb);
            target = await PrepareWifiQuietly(usb, allowRestart: true) ?? usb;
            if (!alreadyOn && target.Serial == usb.Serial) target = await WaitReady(usb.Serial) ?? target;
        }
        if (wantMirror && mirrorsOpen == 0) StartMirror(target);
    }

    static async Task<bool> WifiAlreadyOn(Device usb)
    {
        var (_, port) = await Tools.AdbAsync(5000, "-s", usb.Serial, "shell", "getprop", "service.adb.tcp.port");
        return port.Trim() == "5555";
    }

    /// <summary>Makes sure the phone can also be reached over Wi-Fi. Returns the device to mirror (USB if it came back).</summary>
    async Task<Device?> PrepareWifiQuietly(Device usb, bool allowRestart)
    {
        wifiDone.Add(usb.Serial);
        var ip = await PhoneWifiIp(usb.Serial);
        if (ip == null) return null;
        if (!await WifiAlreadyOn(usb))
        {
            if (!allowRestart) return null;
            Log($"Turning on Wi-Fi mode for {usb.Model}…");
            await Tools.AdbAsync("-s", usb.Serial, "tcpip", "5555");
            await Task.Delay(1800);
        }
        var (_, o) = await Tools.AdbAsync(8000, "connect", $"{ip}:5555");
        if (o.Contains("connected to"))
        {
            cfg.LastIp = ip;
            cfg.Save();
            Log($"Wi-Fi ready for {usb.Model} ({ip}). You can unplug anytime.", "good");
        }
        var back = await WaitReady(usb.Serial);
        if (back != null) return back;
        var all = await Tools.DevicesAsync();
        return all.FirstOrDefault(x => x.Ready && x.Serial == $"{ip}:5555");
    }

    static async Task<Device?> WaitReady(string serial)
    {
        for (int i = 0; i < 12; i++)
        {
            var d = (await Tools.DevicesAsync()).FirstOrDefault(x => x.Serial == serial && x.Ready);
            if (d != null) return d;
            await Task.Delay(500);
        }
        return null;
    }

    Device? DefaultDevice => devices.FirstOrDefault(x => x.Ready && !x.Wireless) ?? devices.FirstOrDefault(x => x.Ready);

    // ================= mirroring =================

    void StartMirror(Device? target)
    {
        if (!engineReady || Tools.Scrcpy == null) { Toast("Still getting ready…"); return; }
        var d = target ?? DefaultDevice;
        if (d == null) { Toast("Plug in your phone first"); return; }
        if (!d.Ready)
        {
            Log(d.State == "unauthorized"
                ? "Unlock your phone and tap \"Allow\" on the USB debugging prompt first."
                : "That phone is offline. Unplug and replug it, or reconnect Wi-Fi.", "warn");
            return;
        }

        int size = cfg.MaxSize;
        string bitrate = d.Wireless ? "6M" : "8M";
        if (size < 0)
        {
            size = d.Wireless ? 1280 : 1920;
            bitrate = d.Wireless ? "4M" : "10M";
        }

        var args = new List<string>
        {
            $"--serial={d.Serial}",
            $"--window-title={d.Model} — Phone Mirror",
            $"--video-bit-rate={bitrate}",
            "--max-fps=60",
        };
        if (size > 0) args.Add($"--max-size={size}");
        if (cfg.StayAwake) args.Add("--stay-awake");
        if (cfg.ScreenOff) args.Add("--turn-screen-off");
        if (!cfg.Audio) args.Add("--no-audio");
        bool framed = cfg.FrameStyle != "Off" && !cfg.Fullscreen;
        if (framed) args.Add("--window-borderless");
        if (cfg.AlwaysOnTop && !framed) args.Add("--always-on-top");
        if (cfg.Fullscreen) args.Add("--fullscreen");
        string? rec = null;
        if (cfg.Record)
        {
            Directory.CreateDirectory(RecordDir);
            rec = Path.Combine(RecordDir, $"{Safe(d.Model)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            args.Add($"--record={rec}");
        }

        var psi = new ProcessStartInfo(Tools.Scrcpy)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Tools.Scrcpy) ?? "",
        };
        psi.Environment["ADB_MDNS_OPENSCREEN"] = "1";
        foreach (var a in args) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = DateTime.Now;
        var output = new List<string>();
        PhoneFrameForm? frame = null;
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (output) { output.Add(line.Trim()); if (output.Count > 40) output.RemoveAt(0); }
            if (line.Contains("ERROR")) Ui(() => Log("  " + line.Trim(), "error"));
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => Ui(() =>
        {
            mirrorsOpen--;
            mirroring.Remove(d.Serial);
            int code = 0;
            try { code = p.ExitCode; } catch { }
            frame?.ProcessExited();
            p.Dispose();
            PushState();
            if (rec != null && File.Exists(rec))
            {
                Log($"Recording saved: {rec}", "good");
                try { Process.Start("explorer.exe", $"/select,\"{rec}\""); } catch { }
            }
            if (code == 2 && !d.Wireless && cfg.AutoWifi && !closing)
            {
                _ = Handoff(d);
            }
            else if (code != 0 && (DateTime.Now - started).TotalSeconds < 20)
            {
                Log($"The mirror for {d.Model} couldn't start. Details:", "error");
                List<string> last;
                lock (output) last = output.Where(l => !l.StartsWith("INFO")).TakeLast(4).ToList();
                if (last.Count == 0) lock (output) last = output.TakeLast(4).ToList();
                foreach (var l in last) Log("  " + l, "error");
                Log("Tip: unlock the phone and look for an \"Allow\" prompt, or try Quality › Light.", "warn");
            }
            else Log($"Closed mirror for {d.Model}.");
        });

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            mirrorsOpen++;
            mirroring.Add(d.Serial);
            PushState();
            Log($"Opening {d.Model} ({d.Connection})…{(rec != null ? " recording." : "")}");
            if (framed)
            {
                frame = new PhoneFrameForm(p, d, cfg.FrameStyle, cfg.AlwaysOnTop, cfg.ButtonStrip)
                {
                    ScreenshotRequested = dev => Screenshot(dev),
                    Log = msg => Log(msg, "warn"),
                };
                _ = AttachFrame(frame, p);
            }
        }
        catch (Exception ex) { Log("Couldn't launch the mirror: " + ex.Message, "error"); }
    }

    async Task AttachFrame(PhoneFrameForm frame, Process p)
    {
        bool ok = false;
        try { ok = await frame.AttachAsync(Screen.FromControl(this)); }
        catch (Exception ex) { Log("Phone frame problem: " + ex.Message, "warn"); }
        if (!ok)
        {
            try { frame.Dispose(); } catch { }
            if (!p.HasExited) Log("Showing the mirror in a plain window instead.", "warn");
        }
    }

    async Task Handoff(Device usb)
    {
        Log($"{usb.Model} unplugged. Switching to Wi-Fi…");
        for (int i = 0; i < 8 && !closing; i++)
        {
            await Task.Delay(1000);
            var devs = await Tools.DevicesAsync();
            var w = devs.FirstOrDefault(x => x.Wireless && x.Ready && x.Model == usb.Model);
            if (w != null)
            {
                if (mirrorsOpen == 0) StartMirror(w);
                return;
            }
            if (i == 2 && cfg.LastIp != "") await Tools.AdbAsync(4000, "connect", WithPort(cfg.LastIp));
        }
        Log("Couldn't switch to Wi-Fi. Is the phone on the same Wi-Fi as this laptop?", "warn");
    }

    // ================= Wi-Fi =================

    async Task ManualEnableWifi(Device? d)
    {
        var usb = d is { Wireless: false, Ready: true } ? d : devices.FirstOrDefault(x => !x.Wireless && x.Ready);
        if (usb == null) { Toast("Plug in your phone with USB first"); return; }
        if (mirroring.Contains(usb.Serial) && !await WifiAlreadyOn(usb))
        {
            Toast("Close the phone window first. Turning on Wi-Fi briefly restarts the connection.");
            return;
        }
        Log($"Setting up Wi-Fi for {usb.Model}…");
        var r = await PrepareWifiQuietly(usb, allowRestart: true);
        if (cfg.LastIp == "" || r == null) Log("Couldn't turn on Wi-Fi. Is the phone connected to Wi-Fi?", "warn");
        await RefreshDevices();
    }

    static async Task<string?> PhoneWifiIp(string serial)
    {
        var (_, w) = await Tools.AdbAsync("-s", serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0");
        var m = Regex.Match(w, @"inet (\d+\.\d+\.\d+\.\d+)");
        if (m.Success) return m.Groups[1].Value;
        var (_, r) = await Tools.AdbAsync("-s", serial, "shell", "ip", "route");
        foreach (var line in r.Split('\n').OrderByDescending(l => l.Contains("wlan")))
        {
            m = Regex.Match(line, @"src (\d+\.\d+\.\d+\.\d+)");
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    async Task ReconnectWifi()
    {
        if (!engineReady || cfg.LastIp == "" || (DateTime.Now - lastReconnect).TotalSeconds < 15) return;
        if (devices.Any(d => d.Wireless && d.Serial.StartsWith(cfg.LastIp + ":"))) return;
        lastReconnect = DateTime.Now;
        var (_, o) = await Tools.AdbAsync(4000, "connect", WithPort(cfg.LastIp));
        if (o.Contains("connected to")) await RefreshDevices();
    }

    async Task ConnectIp(string input)
    {
        if (input == "") return;
        var target = WithPort(input);
        Log($"Connecting to {target}…");
        var (_, o) = await Tools.AdbAsync(10000, "connect", target);
        if (o.Contains("connected to"))
        {
            if (target.EndsWith(":5555")) { cfg.LastIp = target[..^5]; cfg.Save(); }
            Log("Connected.", "good");
            await RefreshDevices();
        }
        else
        {
            Log("Couldn't reach the phone: " + FirstLine(o), "error");
            Log("Try \"Scan QR code\" instead, or plug in with USB once.", "warn");
        }
    }

    // ================= wireless pairing =================

    void QrStatus(string text, string level, bool done = false) => Post(new { type = "qrStatus", text, level, done });

    async Task StartQr()
    {
        qrCts?.Cancel();
        var cts = new CancellationTokenSource();
        qrCts = cts;
        if (!engineReady) { QrStatus("Still getting ready. Try again in a moment.", "bad"); return; }

        var name = "phonemirror-" + RandomNumberGenerator.GetHexString(6, true);
        var pass = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 10);
        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode($"WIFI:T:ADB;S:{name};P:{pass};;", QRCodeGenerator.ECCLevel.M))
        {
            var png = new PngByteQRCode(data).GetGraphic(8);
            Post(new { type = "qr", image = "data:image/png;base64," + Convert.ToBase64String(png) });
        }

        try { await QrPairLoop(name, pass, cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { QrStatus("Something went wrong: " + ex.Message, "bad"); }
    }

    async Task QrPairLoop(string name, string pass, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddMinutes(3);
        string? pairAddr = null;
        while (pairAddr == null)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.Now > deadline)
            {
                QrStatus("Timed out. Check the phone and laptop are on the same Wi-Fi, then try again.", "bad");
                return;
            }
            var found = await Mdns.BrowseAsync("_adb-tls-pairing._tcp.local", 1500, ct);
            var hit = found.FirstOrDefault(f => f.Instance.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { pairAddr = hit.Endpoint; break; }

            var (_, o) = await Tools.AdbAsync(4000, "mdns", "services");
            foreach (var line in o.Split('\n'))
                if (line.Contains(name) && line.Contains("_adb-tls-pairing"))
                {
                    var m = AddrRx.Match(line);
                    if (m.Success) pairAddr = m.Value;
                }
        }

        QrStatus("Phone found. Pairing…", "wait");
        Log($"Pairing with {pairAddr}…");
        var (_, r) = await Tools.AdbAsync(20000, "pair", pairAddr, pass);
        ct.ThrowIfCancellationRequested();
        if (!r.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            QrStatus("Pairing failed: " + FirstLine(r), "bad");
            Log("Pairing failed: " + FirstLine(r), "error");
            return;
        }

        QrStatus("Paired! Connecting…", "wait");
        var d = await ConnectAfterPairing(pairAddr.Split(':')[0], ct);
        if (d != null)
        {
            QrStatus($"Connected to {d.Model}", "good", done: true);
            Log($"{d.Model} added wirelessly.", "good");
            if (cfg.AutoMirror && mirrorsOpen == 0) StartMirror(d);
        }
        else
        {
            QrStatus("Paired, but couldn't connect. Use \"Connect by IP\" with the address on the Wireless debugging screen.", "bad");
            Log("Paired, but couldn't connect automatically.", "warn");
        }
    }

    async Task PairWithCode(string addr, string code)
    {
        if (!AddrRx.IsMatch(addr)) { Log("That address should look like 192.168.1.20:37099 (IP, colon, port).", "warn"); return; }
        Log($"Pairing with {addr}…");
        var (_, o) = await Tools.AdbAsync(20000, "pair", addr, code);
        if (!o.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            Log("Pairing failed: " + FirstLine(o), "error");
            Log("Codes expire quickly. Open a fresh pairing code and try again.", "warn");
            return;
        }
        Log("Paired! Connecting…", "good");
        var d = await ConnectAfterPairing(addr.Split(':')[0], CancellationToken.None);
        if (d != null)
        {
            Log($"{d.Model} added wirelessly.", "good");
            if (cfg.AutoMirror && mirrorsOpen == 0) StartMirror(d);
        }
        else Log("Paired, but couldn't connect. Use \"Connect by IP\" with the address on the Wireless debugging screen.", "warn");
    }

    /// <summary>After pairing, find the phone's connect port (it differs from the pairing port) and connect.</summary>
    async Task<Device?> ConnectAfterPairing(string ip, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            var devs = await Tools.DevicesAsync();
            var d = devs.FirstOrDefault(x => x.Ready && x.Wireless && (x.Serial.StartsWith(ip + ":") || x.Serial.Contains("_adb-tls-connect")));
            if (d != null) { await RefreshDevices(); return d; }

            var found = await Mdns.BrowseAsync("_adb-tls-connect._tcp.local", 1500, ct);
            foreach (var f in found.Where(f => f.Address.ToString() == ip))
                await Tools.AdbAsync(8000, "connect", f.Endpoint);

            var (_, s) = await Tools.AdbAsync(4000, "mdns", "services");
            foreach (var line in s.Split('\n'))
                if (line.Contains("_adb-tls-connect") && line.Contains(ip + ":"))
                {
                    var m = AddrRx.Match(line);
                    if (m.Success) await Tools.AdbAsync(8000, "connect", m.Value);
                }
            await Task.Delay(800, ct);
        }
        return null;
    }

    async Task Disconnect(Device? d)
    {
        if (d == null || !d.Wireless) return;
        await Tools.AdbAsync("disconnect", d.Serial);
        if (cfg.LastIp != "" && d.Serial.StartsWith(cfg.LastIp + ":")) { cfg.LastIp = ""; cfg.Save(); }
        Log($"Disconnected {d.Model}.");
        await RefreshDevices();
    }

    // ================= extras =================

    async Task Screenshot(Device? target)
    {
        var d = target ?? DefaultDevice;
        if (d is not { Ready: true }) { Toast("Connect a phone first"); return; }
        Directory.CreateDirectory(ShotDir);
        var file = Path.Combine(ShotDir, $"{Safe(d.Model)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        if (await Tools.ScreenshotAsync(d.Serial, file))
        {
            Log($"Screenshot saved: {file}", "good");
            Toast("Screenshot saved");
            try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
        }
        else
        {
            try { File.Delete(file); } catch { }
            Log("Screenshot failed. Is the phone unlocked?", "warn");
        }
    }

    // ================= helpers =================

    void Log(string msg, string level = "")
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        logHistory.Add((time, msg, level));
        if (logHistory.Count > 400) logHistory.RemoveRange(0, 100);
        Post(new { type = "log", time, text = msg, level });
    }

    void Ui(Action a)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    static string WithPort(string ip) => ip.Contains(':') ? ip : ip + ":5555";
    static string FirstLine(string s) => s.Split('\n').FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "no response";
    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
