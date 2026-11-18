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
    public static CoreWebView2Environment? WebEnv { get; private set; }
    static readonly JsonSerializerOptions J = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static readonly Regex AddrRx = new(@"\d+\.\d+\.\d+\.\d+:\d+");

    readonly Settings cfg = Settings.Load();
    bool locked;
    int badTries;
    DateTime lockoutUntil = DateTime.MinValue;
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer appTimer = new() { Interval = 5000 };
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
        locked = HasPin;   // with a PIN, the app always starts locked

        tray.Icon = Icon;
        tray.Text = "Phone Mirror";
        tray.Visible = true;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Phone Mirror", null, (_, _) => ShowWindow());
        trayMenu.Items.Add("Check for updates", null, async (_, _) => { ShowWindow(); await CheckForUpdate(manual: true); });
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
        if (cfg.PcId == "") { cfg.PcId = Guid.NewGuid().ToString("N"); cfg.Save(); }
        DirectLink.Changed += () => Ui(() =>
        {
            if (DirectLink.Error != null && DirectLink.Error != lastDirectError) Log(DirectLink.Error, "warn");
            lastDirectError = DirectLink.Error;
            PushState();
        });
        UpdateDirect();
        _ = DiscoverApps();
        appTimer.Tick += (_, _) => _ = DiscoverApps();
        appTimer.Start();

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
        updateTimer.Tick += (_, _) => _ = CheckForUpdate();
        updateTimer.Start();
    }

    async Task<bool> InitWebView()
    {
        try
        {
            // Allow the player page (on 127.0.0.1) to reach phones on the local Wi-Fi.
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-features=BlockInsecurePrivateNetworkRequests,PrivateNetworkAccessSendPreflights,PrivateNetworkAccessRespectPreflightResults --autoplay-policy=no-user-gesture-required" +
                // Keep the phone connection's timers running when the phone window is minimized or covered.
                " --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Tools.AppDir, "WebView2"), options);
            WebEnv = env;
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
            Lock();
            if (!cfg.TrayTipShown)
            {
                cfg.TrayTipShown = true;
                cfg.Save();
                tray.ShowBalloonTip(4000, "Phone Mirror is still running",
                    "It's in the tray, ready for when you plug in your phone. Right-click › Quit to close it.", ToolTipIcon.Info);
            }
            return;
        }
        List<(Process Proc, PhoneFrameForm? Frame)> open;
        lock (active) open = active.Where(a => { try { return !a.Proc.HasExited; } catch { return false; } }).ToList();
        if (!shutdownDone && open.Count > 0)
        {
            // Close the phone windows first (cleanly, so recordings are saved), then quit.
            e.Cancel = true;
            Hide();
            _ = ShutdownMirrorsThenQuit(open);
            return;
        }
        closing = true;
        poll.Stop();
        appTimer.Stop();
        DirectLink.Stop();
        foreach (var f in appFrames.ToList())
        {
            try { f.Detach(closeMirror: true); f.ForceClose(); } catch { }
        }
        foreach (var h in appHosts.ToList())
        {
            try { h.Close(); } catch { }
        }
        qrCts?.Cancel();
        try { tracker?.Kill(); } catch { }
        tray.Visible = false;
        base.OnFormClosing(e);
    }

    async Task ShutdownMirrorsThenQuit(List<(Process Proc, PhoneFrameForm? Frame)> open)
    {
        closing = true;                                        // stop reacting to mirrors as they exit
        poll.Stop();
        foreach (var (proc, frame) in open)
        {
            if (frame != null) frame.Detach(closeMirror: true); // hand back the video window and ask scrcpy to quit
            else try { proc.CloseMainWindow(); } catch { }      // plain window: same, via its main window
        }
        var all = Task.WhenAll(open.Select(o => o.Proc.WaitForExitAsync()));
        await Task.WhenAny(all, Task.Delay(2500));
        foreach (var (proc, frame) in open)
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
            try { frame?.ForceClose(); } catch { }
        }
        shutdownDone = true;
        reallyQuit = true;
        Close();
    }

    void ShowWindow()
    {
        if ((DateTime.Now - lastUpdateCheck).TotalMinutes > 20) _ = CheckForUpdate();
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
        // Steady Wi-Fi only while something is being mirrored.
        WifiTuning.Log ??= m => Ui(() => Log(m));
        WifiTuning.Set(appMirroring.Count > 0 || mirroring.Count > 0);
        var v = Updater.Current;
        Post(new
        {
            type = "state",
            version = $"{v.Major}.{v.Minor}.{v.Build}",
            engine = new { ready = engineReady, version = Tools.InstalledVersion },
            phones = PhonesView(),
            settings = cfg,
            update = updateVersion,
            locked,
            hasPin = HasPin,
            direct = new { allowed = cfg.AllowDirect, running = DirectLink.Running, error = DirectLink.Error, pc = Environment.MachineName },
            displayHz = DisplayHz(),
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
            string Id() => Str("id") ?? "";
            KnownPhone? Ph() => cfg.Phones.FirstOrDefault(p => p.Id == Id());

            var cmd = Str("cmd");
            // While locked, only the lock screen works.
            if (locked && cmd is not ("ready" or "unlock")) return;
            switch (cmd)
            {
                case "unlock": Unlock(Str("pin") ?? ""); break;
                case "lockNow": Lock(); break;
                case "setPin": SetPin(Str("old") ?? "", Str("pin") ?? ""); break;
                case "removePin": RemovePin(Str("old") ?? ""); break;
                case "ready":
                    uiReady = true;
                    Post(new { type = "logHistory", items = logHistory.Select(l => new { time = l.Time, text = l.Text, level = l.Level }) });
                    PushState();
                    break;
                case "mirror":
                    if (Id().StartsWith("app:"))
                    {
                        if (Ph() is KnownPhone ap) await StartAppMirror(ap);
                        break;
                    }
                    var best = BestFor(Id());
                    if (best == null && Ph() is KnownPhone off && await Reconnect(off)) best = BestFor(Id());
                    if (best != null) StartMirror(best);
                    break;
                case "screenshot": await Screenshot(BestFor(Id())); break;
                case "enableWifi": await ManualEnableWifi(ConnsFor(Id()).FirstOrDefault(d => d.Ready && !d.Wireless)); break;
                case "disconnect": await Disconnect(Id()); break;
                case "reconnect": if (Ph() is KnownPhone rp) await Reconnect(rp); break;
                case "appQr":
                    sweepUntil = DateTime.Now.AddMinutes(10);   // look harder while the phone app is being set up
                    using (var gen = new QRCodeGenerator())
                    using (var data = gen.CreateQrCode(ApkUrl, QRCodeGenerator.ECCLevel.M))
                        Post(new { type = "appQr", image = "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8)), url = ApkUrl });
                    break;
                case "rename":
                    if (Ph() is KnownPhone np) { np.Name = (Str("name") ?? "").Trim(); cfg.Save(); PushState(); Toast("Saved"); }
                    break;
                case "phoneSetting":
                    if (Ph() is KnownPhone sp)
                    {
                        var val = r.GetProperty("value");
                        if (Str("key") == "look") sp.Look = val.ValueKind == JsonValueKind.String ? val.GetString() ?? "" : "";
                        if (Str("key") == "quality") sp.Quality = val.ValueKind == JsonValueKind.Number ? val.GetInt32() : null;
                        if (Str("key") == "resetWindow") { sp.WindowSize = 0; sp.WindowX = sp.WindowY = int.MinValue; Toast("Window size reset"); }
                        cfg.Save();
                        PushState();
                    }
                    break;
                case "forget":
                    if (Ph() is KnownPhone fp)
                    {
                        await Disconnect(fp.Id);
                        cfg.Phones.Remove(fp);
                        if (fp.LastIp != "" && cfg.LastIp == fp.LastIp) cfg.LastIp = "";
                        cfg.Save();
                        Log($"Forgot {fp.DisplayName}.");
                        PushState();
                    }
                    break;
                case "setting": ApplySetting(Str("key") ?? "", r.GetProperty("value")); break;
                case "connectIp": await ConnectIp(Str("ip") ?? ""); break;
                case "pairCode": await PairWithCode(Str("addr") ?? "", Str("code") ?? ""); break;
                case "qrStart": await StartQr(); break;
                case "qrCancel": qrCts?.Cancel(); break;
                case "openFolder": OpenFolder(Str("which") == "shots" ? ShotDir : RecordDir); break;
                case "updateEngine": await UpdateEngine(); break;
                case "applyUpdate": await ApplyUpdate(); break;
                case "checkUpdate": await CheckForUpdate(manual: true); break;
                case "copyLog":
                    Clipboard.SetText(string.Join("\r\n", logHistory.Select(l => $"{l.Time}  {l.Text}")) + "\r\n");
                    Toast("Activity copied");
                    break;
            }
        }
        catch (Exception ex) { Log("Error: " + ex.Message, "error"); }
    }

    // ================= PIN lock =================

    bool HasPin => cfg.PinHash != "" && cfg.PinSalt != "";

    static string HashPin(string pin, string salt) => Convert.ToBase64String(
        Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(salt), 200_000, HashAlgorithmName.SHA256, 32));

    bool PinMatches(string pin)
    {
        if (!HasPin) return true;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(HashPin(pin, cfg.PinSalt)), Convert.FromBase64String(cfg.PinHash));
        }
        catch { return false; }
    }

    void Lock()
    {
        if (!HasPin || locked) return;
        locked = true;
        PushState();
    }

    void Unlock(string pin)
    {
        if (DateTime.Now < lockoutUntil)
        {
            Post(new { type = "lockError", text = $"Too many tries. Try again in {(int)(lockoutUntil - DateTime.Now).TotalSeconds + 1} s." });
            return;
        }
        if (PinMatches(pin))
        {
            locked = false;
            badTries = 0;
            PushState();
            return;
        }
        if (++badTries >= 5)
        {
            badTries = 0;
            lockoutUntil = DateTime.Now.AddSeconds(30);
            Post(new { type = "lockError", text = "Too many tries. Wait 30 seconds." });
        }
        else Post(new { type = "lockError", text = "That's not the right PIN." });
    }

    static bool ValidPin(string pin) => pin.Length is >= 4 and <= 8 && pin.All(char.IsDigit);

    void SetPin(string old, string pin)
    {
        if (HasPin && !PinMatches(old)) { Toast("Your current PIN isn't right"); return; }
        if (!ValidPin(pin)) { Toast("Use 4 to 8 digits"); return; }
        var salt = RandomNumberGenerator.GetBytes(16);
        cfg.PinSalt = Convert.ToBase64String(salt);
        cfg.PinHash = HashPin(pin, cfg.PinSalt);
        cfg.Save();
        Toast("PIN set. Phone Mirror will ask for it when it opens.");
        PushState();
    }

    void RemovePin(string old)
    {
        if (!PinMatches(old)) { Toast("That's not the right PIN"); return; }
        cfg.PinHash = cfg.PinSalt = "";
        cfg.Save();
        Toast("PIN removed");
        PushState();
    }

    /// <summary>Mirroring needs the app unlocked. Returns true (and asks for the PIN) if it's locked.</summary>
    bool BlockedByLock()
    {
        if (!locked) return false;
        ShowWindow();
        if (!Visible) return true;
        Toast("Enter your PIN to mirror");
        return true;
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
            case "frameRate": cfg.FrameRate = value.GetInt32(); break;
            case "frameStyle": cfg.FrameStyle = value.GetString() ?? "Black"; break;
            case "allowDirect":
                cfg.AllowDirect = B();
                cfg.Save();
                if (cfg.AllowDirect) DirectLink.Stop();
                UpdateDirect(retry: true);
                break;
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

    DateTime lastUpdateCheck = DateTime.MinValue;
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 3 * 60 * 60 * 1000 };   // every 3 hours

    /// <summary>Looks for a newer version on GitHub. Runs at start, every few hours, whenever the window is opened, and on request.</summary>
    async Task CheckForUpdate(bool manual = false)
    {
        lastUpdateCheck = DateTime.Now;
        try
        {
            var u = await Updater.CheckAsync();
            if (u == null)
            {
                if (manual) Toast("You have the latest version");
                return;
            }
            bool isNew = updateVersion == null;
            updateUrl = u.Value.Url;
            updateVersion = $"{u.Value.Version.Major}.{u.Value.Version.Minor}.{u.Value.Version.Build}";
            PushState();
            if (isNew && !Visible)
                tray.ShowBalloonTip(5000, "Phone Mirror update", $"Version {updateVersion} is ready. Open Phone Mirror to update.", ToolTipIcon.Info);
        }
        catch
        {
            if (manual) Toast("Couldn't check for updates. Are you online?");
        }
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

        // Remember every phone we see (one entry per phone, even when it's on USB and Wi-Fi at once).
        bool saved = false;
        foreach (var d in now.Where(x => x.Ready))
        {
            var id = await IdOf(d);
            if (id.StartsWith("serial:")) continue;
            var ph = cfg.Phones.FirstOrDefault(p => p.Id == id);
            if (ph == null)
            {
                ph = new KnownPhone { Id = id, Model = d.Model };
                cfg.Phones.Add(ph);
                Log($"Saved {d.Model} to My phones.", "good");
            }
            ph.Model = d.Model;
            ph.LastSeen = DateTime.Now;
            if (!d.Wireless) paused.Remove(id);
            if (d.Wireless && d.Serial.EndsWith(":5555")) ph.LastIp = d.Serial[..^5];
            saved = true;
        }
        if (saved) cfg.Save();
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
            if (PhoneFor(usb) is KnownPhone ph) ph.LastIp = ip;
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

    // ================= my phones =================

    readonly Dictionary<string, string> hwIds = new();
    readonly HashSet<string> paused = new();   // phones you disconnected on purpose: don't auto-reconnect

    /// <summary>A phone's hardware serial, so its USB and Wi-Fi connections count as one phone.</summary>
    async Task<string> IdOf(Device d)
    {
        if (hwIds.TryGetValue(d.Serial, out var id)) return id;
        if (!d.Ready) return "serial:" + d.Serial;
        var (_, o) = await Tools.AdbAsync(5000, "-s", d.Serial, "shell", "getprop", "ro.serialno");
        var s = o.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l != "") ?? "";
        if (s.Length < 3 || s.Contains(' ') || s.Contains(':')) s = d.Wireless ? "serial:" + d.Serial : d.Serial;
        hwIds[d.Serial] = s;
        return s;
    }

    string KeyOf(Device d) => hwIds.TryGetValue(d.Serial, out var id) ? id : "serial:" + d.Serial;
    KnownPhone? PhoneFor(Device d) => hwIds.TryGetValue(d.Serial, out var id) ? cfg.Phones.FirstOrDefault(p => p.Id == id) : null;
    IEnumerable<Device> ConnsFor(string id) => devices.Where(d => KeyOf(d) == id);
    Device? BestFor(string id) => ConnsFor(id).FirstOrDefault(d => d.Ready && !d.Wireless) ?? ConnsFor(id).FirstOrDefault(d => d.Ready);
    bool Online(KnownPhone p) => p.IsApp ? AppOnline(p) : ConnsFor(p.Id).Any(d => d.Ready);

    object PhonesView()
    {
        var groups = devices.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<(bool Online, DateTime Seen, object View)>();
        var listed = new HashSet<string>();

        object View(string id, KnownPhone? p, List<Device> conns)
        {
            bool isApp = p?.IsApp == true;
            bool online = isApp ? AppOnline(p!) : conns.Any(c => c.Ready);
            string status = online ? "ready"
                : conns.Any(c => c.State == "unauthorized") ? "unauthorized"
                : conns.Any(c => c.State == "offline") ? "offline" : "away";
            var model = p?.Model ?? conns.FirstOrDefault()?.Model ?? "Android phone";
            return new
            {
                id,
                name = p?.DisplayName ?? model,
                nickname = p?.Name ?? "",
                model,
                saved = p != null,
                online,
                status,
                usb = conns.Any(c => c.Ready && !c.Wireless),
                wifi = conns.Any(c => c.Ready && c.Wireless),
                mirroring = isApp ? appMirroring.Contains(id) : conns.Any(c => mirroring.Contains(c.Serial)),
                app = isApp,
                appOutdated = isApp && p!.AppVersion > 0 && p.AppVersion < Updater.Current.Build,
                lastSeen = p == null ? "" : Seen(p.LastSeen),
                lastIp = p?.LastIp ?? "",
                look = p?.Look ?? "",
                quality = p?.Quality,
                customWindow = p != null && p.WindowSize > 0,
            };
        }

        foreach (var p in cfg.Phones)
        {
            groups.TryGetValue(p.Id, out var conns);
            conns ??= new List<Device>();
            listed.Add(p.Id);
            rows.Add((Online(p), p.LastSeen, View(p.Id, p, conns)));
        }
        foreach (var g in groups.Where(g => !listed.Contains(g.Key)))
            rows.Add((g.Value.Any(c => c.Ready), DateTime.Now, View(g.Key, null, g.Value)));

        return rows.OrderByDescending(r => r.Online).ThenByDescending(r => r.Seen).Select(r => r.View).ToList();
    }

    static string Seen(DateTime t)
    {
        if (t == default) return "";
        if (t.Date == DateTime.Today) return "today at " + t.ToString("h:mm tt");
        if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        return t.ToString("MMM d");
    }

    /// <summary>Tries to bring a saved phone back over Wi-Fi.</summary>
    async Task<bool> Reconnect(KnownPhone p)
    {
        paused.Remove(p.Id);
        if (Online(p)) return true;
        if (p.IsApp)
        {
            await DiscoverApps(force: true);
            if (Online(p)) return true;
            Toast($"Open Phone Mirror on {p.DisplayName}");
            Log($"Couldn't find {p.DisplayName}. Open the Phone Mirror app on the phone, on the same Wi-Fi.", "warn");
            return false;
        }
        Log($"Looking for {p.DisplayName}…");
        if (p.LastIp != "")
        {
            var (_, o) = await Tools.AdbAsync(5000, "connect", p.LastIp + ":5555");
            if (o.Contains("connected to")) { await RefreshDevices(); if (Online(p)) return true; }
        }
        // Phones paired with a QR code or pairing code announce themselves on the network.
        var found = await Mdns.BrowseAsync("_adb-tls-connect._tcp.local", 1500, CancellationToken.None);
        foreach (var f in found)
            await Tools.AdbAsync(6000, "connect", f.Endpoint);
        await Task.Delay(500);
        await RefreshDevices();
        if (Online(p)) { Log($"{p.DisplayName} is back.", "good"); return true; }
        Log($"Couldn't reach {p.DisplayName}. Plug it in once, or turn on Wireless debugging on the phone.", "warn");
        Toast($"Couldn't reach {p.DisplayName}");
        return false;
    }

    // ================= phone app (no debugging) =================

    const string ApkUrl = "https://github.com/willhrom/PhoneMirror/releases/latest/download/PhoneMirror.apk";

    class AppPhone
    {
        public string Id = "", Name = "", Ip = "";
        public int Port;
        public DateTime Seen;
    }

    record AppInfo(string Id, string Name, string Ip, int Port, long Version = 0);

    readonly Dictionary<string, AppPhone> appPhones = new();
    readonly HashSet<string> appMirroring = new();
    readonly List<PhoneFrameForm> appFrames = new();
    readonly List<ScreenHostForm> appHosts = new();   // phone-app mirrors in a plain window
    DateTime lastMdns = DateTime.MinValue, sweepUntil = DateTime.MinValue;
    string? lastDirectError;

    string DirectSsid => "DIRECT-PM-" + (cfg.PcId.Length >= 4 ? cfg.PcId[..4].ToUpperInvariant() : "0000");

    string DirectPass()
    {
        if (cfg.DirectPass.Length < 8)
        {
            cfg.DirectPass = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 12);
            cfg.Save();
        }
        return cfg.DirectPass;
    }

    /// <summary>Runs the private Wi-Fi Direct link while it's useful: allowed, and you have phone-app phones.</summary>
    void UpdateDirect(bool retry = false)
    {
        bool want = cfg.AllowDirect && cfg.Phones.Any(p => p.IsApp) && !closing;
        if (want && !DirectLink.Running && (retry || DirectLink.Error == null)) DirectLink.Start(DirectSsid, DirectPass());
        else if (!want && DirectLink.Running) DirectLink.Stop();
    }
    DateTime lastScan = DateTime.MinValue;
    bool discovering;

    bool AppOnline(KnownPhone p) =>
        p.IsApp && appPhones.TryGetValue(p.Id, out var a) && (DateTime.Now - a.Seen).TotalSeconds < 20;

    /// <summary>Finds phones running the Phone Mirror app: network announcements first, then a quick sweep.</summary>
    /// <param name="force">Look everywhere right now (the user is waiting).</param>
    async Task DiscoverApps(bool force = false)
    {
        if (discovering || closing) return;
        discovering = true;
        try
        {
            bool changed = false;
            bool anyApp = cfg.Phones.Any(p => p.IsApp);
            bool eager = force || DateTime.Now < sweepUntil;   // just set up the app: look harder for a while

            // 1) Network announcements: every 10 s if you use the phone app, else every 30 s.
            if (eager || (DateTime.Now - lastMdns).TotalSeconds >= (anyApp ? 10 : 30))
            {
                lastMdns = DateTime.Now;
                var found = await Mdns.BrowseAsync("_phonemirror._tcp.local", 1200, CancellationToken.None);
                foreach (var f in found)
                {
                    var id = f.Txt.TryGetValue("id", out var i) ? i : f.Instance;
                    long.TryParse(f.Txt.TryGetValue("version", out var v) ? v : "0", out var ver);
                changed |= SeenApp(new AppInfo(id, f.Instance, f.Address.ToString(), f.Port, ver));
                }
            }

            // 2) Saved phones that are offline: one tiny request each (cheap).
            var targets = cfg.Phones.Where(p => p.IsApp && p.LastIp != "" && !AppOnline(p))
                .Select(p => (Ip: p.LastIp, Port: p.Port > 0 ? p.Port : 8765)).ToList();

            // 2b) A phone that joined this laptop's direct link: look on that little network.
            if (DirectLink.Running && !cfg.Phones.Any(AppOnline))
                targets.AddRange(DirectLink.PeerAddresses().Select(ip => (Ip: ip, Port: 8765)));

            // 3) Sweep the local network only when it can help: while setting up, when asked,
            //    or (rarely) when a saved phone may have changed address.
            bool noneFresh = appPhones.Values.All(a => (DateTime.Now - a.Seen).TotalSeconds > 10);
            double sweepEvery = force ? 0 : eager ? 20 : 180;
            if ((eager || (anyApp && noneFresh)) && (DateTime.Now - lastScan).TotalSeconds > sweepEvery)
            {
                lastScan = DateTime.Now;
                targets.AddRange(SubnetHosts().Select(ip => (Ip: ip, Port: 8765)));
            }
            if (targets.Count > 0)
            {
                var infos = await Task.WhenAll(targets.Distinct().Select(t => ProbeApp(t.Ip, t.Port)));
                foreach (var info in infos)
                    if (info != null) changed |= SeenApp(info);
            }

            foreach (var stale in appPhones.Values.Where(a => (DateTime.Now - a.Seen).TotalSeconds > 20).ToList())
            {
                appPhones.Remove(stale.Id);
                changed = true;
            }
            if (changed) PushState();
        }
        catch { }
        finally { discovering = false; }
    }

    bool SeenApp(AppInfo info)
    {
        var key = "app:" + info.Id;
        bool changed = !appPhones.TryGetValue(key, out var a) || a.Ip != info.Ip || (DateTime.Now - a.Seen).TotalSeconds > 15;
        if (a == null) appPhones[key] = a = new AppPhone { Id = key };
        a.Name = info.Name;
        a.Ip = info.Ip;
        a.Port = info.Port;
        a.Seen = DateTime.Now;

        var ph = cfg.Phones.FirstOrDefault(p => p.Id == key);
        if (ph == null)
        {
            ph = new KnownPhone { Id = key, IsApp = true, Model = info.Name };
            cfg.Phones.Add(ph);
            Log($"Found {info.Name} (Phone Mirror app). Saved to My phones.", "good");
            changed = true;
            UpdateDirect();
        }
        ph.IsApp = true;
        ph.Model = info.Name;
        ph.LastIp = info.Ip;
        ph.Port = info.Port;
        if (info.Version > 0) ph.AppVersion = info.Version;
        ph.LastSeen = DateTime.Now;
        if (changed) cfg.Save();
        return changed;
    }

    /// <summary>Other addresses on this PC's local network(s) (the /24 around each real adapter).</summary>
    static IEnumerable<string> SubnetHosts()
    {
        var mine = new HashSet<string>();
        var bases = new List<string>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211
                or System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)) continue;
            var desc = (ni.Description + " " + ni.Name).ToLowerInvariant();
            if (desc.Contains("virtual") || desc.Contains("vethernet") || desc.Contains("vmware") || desc.Contains("hyper-v")
                || desc.Contains("vpn") || desc.Contains("tap") || desc.Contains("bluetooth")) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                mine.Add(ua.Address.ToString());
                var prefix = $"{b[0]}.{b[1]}.{b[2]}.";
                if (!bases.Contains(prefix)) bases.Add(prefix);
            }
        }
        foreach (var prefix in bases.Take(2))
            for (int i = 1; i < 255; i++)
            {
                var ip = prefix + i;
                if (!mine.Contains(ip)) yield return ip;
            }
    }

    /// <summary>Asks one address "are you a Phone Mirror phone?" (a tiny HTTP request).</summary>
    static async Task<AppInfo?> ProbeApp(string ip, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(700);
            await client.ConnectAsync(ip, port, cts.Token);
            var stream = client.GetStream();
            var req = System.Text.Encoding.ASCII.GetBytes($"GET /info HTTP/1.1\r\nHost: {ip}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req, cts.Token);
            var buf = new byte[4096];
            int total = 0, n;
            while (total < buf.Length && (n = await stream.ReadAsync(buf.AsMemory(total), cts.Token)) > 0) total += n;
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, total);
            int body = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (body < 0) return null;
            using var doc = JsonDocument.Parse(text[(body + 4)..]);
            var r = doc.RootElement;
            if (r.GetProperty("app").GetString() != "phonemirror") return null;
            return new AppInfo(r.GetProperty("id").GetString() ?? ip,
                r.TryGetProperty("name", out var nm) ? nm.GetString() ?? "Android phone" : "Android phone",
                ip, r.TryGetProperty("port", out var pt) ? pt.GetInt32() : port,
                r.TryGetProperty("version", out var vv) && vv.TryGetInt64(out var vl) ? vl : 0);
        }
        catch { return null; }
    }

    /// <summary>Video size for the phone app on Auto: the window's long side (a bit over for sharpness).</summary>
    int AutoAppSize(int windowLongSide)
    {
        int screenH = Screen.PrimaryScreen?.Bounds.Height ?? 1080;
        int w = windowLongSide > 0 ? windowLongSide : (int)(screenH * 0.8);
        return Math.Clamp((int)(w * 1.1) / 16 * 16, 640, 1600);
    }

    /// <summary>Enough bits for sharp scrolling at this size, not more (the phone backs off further if Wi-Fi lags).</summary>
    static int AppBitrate(int maxSize, int fps)
    {
        double pixels = maxSize * (maxSize * 0.46);
        return (int)Math.Clamp(pixels * fps * 0.2, 4_000_000, 14_000_000);
    }

    async Task StartAppMirror(KnownPhone ph)
    {
        if (BlockedByLock()) return;
        if (appMirroring.Contains(ph.Id)) { Toast($"{ph.DisplayName} is already open"); return; }
        if (!AppOnline(ph) && !await Reconnect(ph)) return;
        if (WebEnv == null || !appPhones.TryGetValue(ph.Id, out var a)) return;
        LocalWeb.Start();

        // Wi-Fi defaults tuned for low lag. On Auto, the video is sized to the phone window you actually
        // see (no point sending more pixels than it shows) and follows it when you resize.
        int fps = cfg.FrameRate > 0 ? cfg.FrameRate : 60;
        int q = ph.Quality ?? cfg.MaxSize;
        bool autoSize = q < 0;
        int maxSize = autoSize ? AutoAppSize(ph.WindowSize) : q == 0 ? 2400 : q;
        int bitrate = AppBitrate(maxSize, fps);
        // The phone remembers these, so later it can link straight to this laptop without shared Wi-Fi.
        object? direct = cfg.AllowDirect ? new { ssid = DirectSsid, pass = DirectPass() } : null;
        var p = new { url = $"ws://{a.Ip}:{a.Port}/", pcId = cfg.PcId, pcName = Environment.MachineName, name = ph.DisplayName, maxSize, bitrate, fps, audio = cfg.Audio, direct };
        var url = $"http://127.0.0.1:{LocalWeb.Port}/mirror.html#" + Uri.EscapeDataString(JsonSerializer.Serialize(p));

        string look = string.IsNullOrEmpty(ph.Look) ? cfg.FrameStyle : ph.Look;
        bool plain = look == "Off";
        var host = new ScreenHostForm(url, plain, ph.DisplayName, cfg.AlwaysOnTop) { Log = m => Ui(() => Log(m, "warn")) };
        if (plain)
        {
            // Ordinary window: no phone frame or dock (right-click = Back, middle-click = Home).
            appMirroring.Add(ph.Id);
            appHosts.Add(host);
            host.FormClosed += (_, _) => Ui(() =>
            {
                appMirroring.Remove(ph.Id);
                appHosts.Remove(host);
                PushState();
                Log($"Closed mirror for {ph.DisplayName}.");
            });
            PushState();
            Log($"Opening {ph.DisplayName} (phone app)…");
            host.Show();
            return;
        }
        var src = new AppSource(host, ph.DisplayName, ShotDir, m => Ui(() => { Log(m, "good"); Toast("Screenshot saved"); }));
        var pos = ph.WindowX != int.MinValue ? new Point(ph.WindowX, ph.WindowY) : (Point?)null;
        var frame = new PhoneFrameForm(src, look, cfg.AlwaysOnTop, cfg.ButtonStrip, ph.WindowSize, pos)
        {
            Log = m => Log(m, "warn"),
            SaveWindow = (longSide, at) =>
            {
                ph.WindowSize = longSide;
                ph.WindowX = at.X;
                ph.WindowY = at.Y;
                cfg.Save();
                if (autoSize)
                {
                    int want = AutoAppSize(longSide);
                    if (Math.Abs(want - maxSize) > maxSize / 5)
                    {
                        maxSize = want;
                        host.SetQuality(maxSize, AppBitrate(maxSize, fps));
                    }
                }
            },
        };
        appMirroring.Add(ph.Id);
        appFrames.Add(frame);
        host.FormClosed += (_, _) => Ui(() =>
        {
            appMirroring.Remove(ph.Id);
            appFrames.Remove(frame);
            frame.ProcessExited();
            PushState();
            Log($"Closed mirror for {ph.DisplayName}.");
        });
        PushState();
        Log($"Opening {ph.DisplayName} (phone app)…");
        host.Show();   // off-screen until the phone frame places it
        bool ok = false;
        try { ok = await frame.AttachAsync(Screen.FromControl(this)); } catch { }
        if (!ok)
        {
            if (!host.IsDisposed) host.Close();
            Log($"Couldn't connect to {ph.DisplayName}. Is Phone Mirror open on the phone, on the same Wi-Fi?", "warn");
        }
    }

    // ================= mirroring =================

    readonly HashSet<string> noH265 = new();
    readonly List<(Process Proc, PhoneFrameForm? Frame)> active = new();
    bool shutdownDone;

    readonly HashSet<string> noHighProfile = new();

    void StartMirror(Device? target, bool forceH264 = false, bool forceBasic = false)
    {
        if (BlockedByLock()) return;
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

        var phone = PhoneFor(d);
        string look = string.IsNullOrEmpty(phone?.Look) ? cfg.FrameStyle : phone!.Look;
        // H.265 needs about half the bandwidth of H.264 for the same picture: smoother over Wi-Fi.
        bool h265 = d.Wireless && !forceH264 && !(phone?.NoH265 ?? false) && !noH265.Contains(KeyOf(d));

        // Frame rate: match the laptop's display (Auto), or the user's choice.
        int hz = DisplayHz();
        int fps = cfg.FrameRate > 0 ? cfg.FrameRate : d.Wireless ? Math.Clamp(hz, 60, 120) : Math.Clamp(hz, 60, 144);
        bool fast = fps > 60;

        int size = phone?.Quality ?? cfg.MaxSize;
        if (size < 0) size = d.Wireless ? 1280 : 1920;
        // Scrolling moves the whole screen at once, which needs lots of bits to stay sharp (no blocks).
        string bitrate = d.Wireless
            ? (h265 ? (fast ? "16M" : "12M") : (fast ? "20M" : "16M"))
            : (fast ? "40M" : "32M");

        // H.264 "High" profile keeps more detail at the same bit rate than the default Baseline.
        bool highProfile = !h265 && !forceBasic && !(phone?.NoHighProfile ?? false) && !noHighProfile.Contains(KeyOf(d));

        var args = new List<string>
        {
            $"--serial={d.Serial}",
            $"--video-codec={(h265 ? "h265" : "h264")}",
            $"--window-title={phone?.DisplayName ?? d.Model} — Phone Mirror",
            $"--video-bit-rate={bitrate}",
            $"--max-fps={fps}",
        };
        if (highProfile) args.Add("--video-codec-options=profile:int=8");
        if (size > 0) args.Add($"--max-size={size}");
        if (cfg.StayAwake) args.Add("--stay-awake");
        if (cfg.ScreenOff) args.Add("--turn-screen-off");
        if (!cfg.Audio) args.Add("--no-audio");
        bool framed = look != "Off" && !cfg.Fullscreen;
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
            lock (active) active.RemoveAll(a => a.Proc == p);
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
            else if (code != 0 && h265 && (DateTime.Now - started).TotalSeconds < 20 && !closing)
            {
                // This phone can't do H.265: remember that and retry with H.264.
                noH265.Add(KeyOf(d));
                if (phone != null) { phone.NoH265 = true; cfg.Save(); }
                Log($"{d.Model} doesn't support the faster video format over Wi-Fi. Using the standard one.");
                StartMirror(d, forceH264: true, forceBasic: forceBasic);
            }
            else if (code != 0 && highProfile && (DateTime.Now - started).TotalSeconds < 20 && !closing)
            {
                // This phone's encoder doesn't accept the High profile: remember and retry with defaults.
                noHighProfile.Add(KeyOf(d));
                if (phone != null) { phone.NoHighProfile = true; cfg.Save(); }
                Log($"Using {d.Model}'s standard video settings.");
                StartMirror(d, forceH264: forceH264, forceBasic: true);
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
            lock (active) active.Add((p, null));
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }   // smoother video decoding
            mirrorsOpen++;
            mirroring.Add(d.Serial);
            PushState();
            Log($"Opening {d.Model} ({d.Connection})…{(rec != null ? " recording." : "")}");
            if (framed)
            {
                var pos = phone != null && phone.WindowX != int.MinValue ? new Point(phone.WindowX, phone.WindowY) : (Point?)null;
                frame = new PhoneFrameForm(new ScrcpySource(p, d, phone?.DisplayName ?? d.Model, dev => Screenshot(dev)), look, cfg.AlwaysOnTop, cfg.ButtonStrip, phone?.WindowSize ?? 0, pos)
                {
                    SaveWindow = (longSide, at) =>
                    {
                        if (phone == null) return;
                        phone.WindowSize = longSide;
                        phone.WindowX = at.X;
                        phone.WindowY = at.Y;
                        cfg.Save();
                    },
                    Log = msg => Log(msg, "warn"),
                };
                lock (active)
                {
                    active.RemoveAll(a => a.Proc == p);
                    active.Add((p, frame));
                }
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
        var id = KeyOf(usb);
        var lastIp = PhoneFor(usb)?.LastIp is { Length: > 0 } ip ? ip : cfg.LastIp;
        Log($"{usb.Model} unplugged. Switching to Wi-Fi…");
        for (int i = 0; i < 8 && !closing; i++)
        {
            await Task.Delay(1000);
            foreach (var w in (await Tools.DevicesAsync()).Where(x => x.Wireless && x.Ready))
            {
                if (await IdOf(w) != id) continue;
                if (mirrorsOpen == 0) StartMirror(w);
                return;
            }
            if (i == 2 && lastIp != "") await Tools.AdbAsync(4000, "connect", WithPort(lastIp));
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

    /// <summary>Quietly re-attaches saved phones when they're back on the network.</summary>
    async Task ReconnectWifi()
    {
        if (!engineReady || (DateTime.Now - lastReconnect).TotalSeconds < 15) return;
        var ips = cfg.Phones.Where(p => !p.IsApp && p.LastIp != "" && !Online(p) && !paused.Contains(p.Id)).Select(p => p.LastIp).ToList();
        if (cfg.LastIp != "" && !cfg.Phones.Any(p => p.LastIp == cfg.LastIp && paused.Contains(p.Id))) ips.Add(cfg.LastIp);
        ips = ips.Distinct().Where(ip => !devices.Any(d => d.Serial.StartsWith(ip + ":"))).Take(6).ToList();
        if (ips.Count == 0) return;
        lastReconnect = DateTime.Now;
        var results = await Task.WhenAll(ips.Select(ip => Tools.AdbAsync(3500, "connect", ip + ":5555")));
        if (results.Any(r => r.Output.Contains("connected to"))) await RefreshDevices();
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

    async Task Disconnect(string id)
    {
        var wireless = ConnsFor(id).Where(d => d.Wireless).ToList();
        paused.Add(id);
        foreach (var d in wireless)
        {
            await Tools.AdbAsync("disconnect", d.Serial);
            if (cfg.LastIp != "" && d.Serial.StartsWith(cfg.LastIp + ":")) { cfg.LastIp = ""; cfg.Save(); }
        }
        if (wireless.Count > 0) Log($"Disconnected {wireless[0].Model} from Wi-Fi.");
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

    /// <summary>Refresh rate of the display Phone Mirror is on (60 if unknown), so USB mirroring can match a 90/120/144 Hz screen.</summary>
    int DisplayHz()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            var screen = Visible ? Screen.FromControl(this) : Screen.PrimaryScreen;
            if (screen != null && EnumDisplaySettings(screen.DeviceName, -1 /* current */, ref dm) && dm.dmDisplayFrequency > 1)
                return dm.dmDisplayFrequency;
        }
        catch { }
        return 60;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

    static string WithPort(string ip) => ip.Contains(':') ? ip : ip + ":5555";
    static string FirstLine(string s) => s.Split('\n').FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "no response";
    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
