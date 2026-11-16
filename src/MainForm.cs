using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PhoneMirror;

public class MainForm : Form
{
    readonly Settings cfg = Settings.Load();
    readonly ListView list = new();
    readonly TextBox log = new();
    readonly ProgressBar progress = new();
    readonly Label status = new();
    readonly Button mirrorBtn;
    readonly FlowLayoutPanel updateBar;
    readonly Label updateLabel = new();
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer poll = new() { Interval = 5000 };
    readonly List<Control> needsEngine = new();
    readonly HashSet<string> wifiDone = new();

    List<Device> devices = new();
    string deviceSignature = "";
    bool refreshing, refreshAgain, engineReady, allowAuto, closing, reallyQuit;
    int mirrorsOpen;
    DateTime lastReconnect = DateTime.MinValue;
    string? updateUrl;
    Process? tracker;

    static string RecordDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Phone Mirror");
    static string ShotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Phone Mirror");

    static readonly (string Label, int Value)[] Sizes =
    {
        ("Auto (recommended)", -1), ("Full resolution", 0), ("1920 (sharp)", 1920), ("1600", 1600),
        ("1280 (smooth)", 1280), ("1024", 1024), ("800 (slow Wi-Fi)", 800),
    };

    static readonly (string Label, string Value)[] Looks =
    {
        ("Phone — black", "Black"), ("Phone — silver", "Silver"), ("Phone — blue", "Blue"), ("Plain window", "Off"),
    };

    public MainForm()
    {
        var v = Updater.Current;
        Text = $"Phone Mirror {v.Major}.{v.Minor}.{v.Build}";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(660, 780);
        MinimumSize = new Size(600, 660);
        StartPosition = FormStartPosition.CenterScreen;
        if (Program.Background) Opacity = 0;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(22, 16, 22, 14),
            BackColor = Theme.Bg,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, SizeType type = SizeType.AutoSize, float h = 0)
        {
            root.RowStyles.Add(new RowStyle(type, h));
            c.Dock = DockStyle.Fill;
            root.Controls.Add(c);
        }

        // ---- header ----
        var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titleBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0), WrapContents = false };
        titleBox.Controls.Add(new Label { Text = "Phone Mirror", AutoSize = true, Font = new Font("Segoe UI Semibold", 20f), Margin = new Padding(0) });
        titleBox.Controls.Add(new Label { Text = "Plug in your phone and it opens here automatically", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 0) });
        var help = Theme.Button("Phone setup help");
        help.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        help.Margin = new Padding(0, 10, 0, 0);
        help.Click += (_, _) => ShowHelp();
        header.Controls.Add(titleBox, 0, 0);
        header.Controls.Add(help, 1, 0);
        Row(header);

        // ---- update banner (hidden until needed) ----
        updateBar = new FlowLayoutPanel { AutoSize = true, BackColor = Color.FromArgb(30, 58, 110), Padding = new Padding(10, 6, 6, 0), Margin = new Padding(0, 10, 0, 0), Visible = false };
        updateLabel.AutoSize = true;
        updateLabel.ForeColor = Color.White;
        updateLabel.Margin = new Padding(0, 6, 12, 0);
        var updateBtn = Theme.Button("Update now");
        updateBtn.BackColor = Theme.Accent;
        updateBtn.Click += async (_, _) => await ApplyUpdate();
        updateBar.Controls.Add(updateLabel);
        updateBar.Controls.Add(updateBtn);
        Row(updateBar);

        // ---- phones ----
        Row(Theme.Heading("Your phones"));
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Theme.Panel;
        list.ForeColor = Theme.Text;
        list.Font = new Font("Segoe UI", 10f);
        list.OwnerDraw = true;
        list.Columns.Add("Phone", 230);
        list.Columns.Add("Connection", 100);
        list.Columns.Add("Status", 200);
        var headerFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Theme.Panel2);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, headerFont,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.Resize += (_, _) => FitColumns();
        list.DoubleClick += (_, _) => StartMirror();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Start mirroring", null, (_, _) => StartMirror());
        menu.Items.Add("Take screenshot", null, async (_, _) => await Screenshot());
        menu.Items.Add("Enable Wi-Fi for this phone", null, async (_, _) => await ManualEnableWifi());
        menu.Items.Add("Disconnect (Wi-Fi)", null, async (_, _) => await Disconnect());
        list.ContextMenuStrip = menu;
        Row(list, SizeType.Absolute, 130);

        var connectRow = Flow();
        connectRow.Margin = new Padding(0, 10, 0, 0);
        var qrBtn = Theme.Button("Add phone wirelessly (QR code)");
        qrBtn.Click += async (_, _) => await PairQr();
        var ipBtn = Theme.Button("Connect by IP…");
        ipBtn.Click += async (_, _) => await ConnectWifi();
        connectRow.Controls.AddRange(new Control[] { qrBtn, ipBtn });
        needsEngine.AddRange(new Control[] { qrBtn, ipBtn });
        Row(connectRow);

        // ---- big button ----
        mirrorBtn = Theme.Button("▶   Start Mirroring", accent: true);
        mirrorBtn.Margin = new Padding(0, 6, 0, 4);
        mirrorBtn.Click += (_, _) => StartMirror();
        Row(mirrorBtn, SizeType.Absolute, 62);

        // ---- automatic ----
        Row(Theme.Heading("Automatic"));
        var auto = Flow();
        auto.Controls.Add(Check("Open when phone is plugged in", cfg.AutoMirror, x => cfg.AutoMirror = x,
            "Start mirroring as soon as you plug in your phone."));
        auto.Controls.Add(Check("Go wireless when unplugged", cfg.AutoWifi, x => cfg.AutoWifi = x,
            "When you plug in, Phone Mirror also turns on Wi-Fi mode. Unplug and the mirror keeps going over Wi-Fi."));
        auto.Controls.Add(Check("Start with Windows", cfg.StartWithWindows, x => { cfg.StartWithWindows = x; Installer.SetStartWithWindows(x); },
            "Run quietly in the system tray so plugging in your phone just works."));
        Row(auto);

        // ---- options ----
        Row(Theme.Heading("Mirror options"));
        var opts = Flow();
        opts.Controls.Add(Check("Turn phone screen off", cfg.ScreenOff, x => cfg.ScreenOff = x,
            "The phone's display goes dark to save battery, but it keeps working here."));
        opts.Controls.Add(Check("Phone audio", cfg.Audio, x => cfg.Audio = x, "Play the phone's sound on the laptop (Android 11+)."));
        opts.Controls.Add(Check("Keep phone awake", cfg.StayAwake, x => cfg.StayAwake = x, "Stop the phone from sleeping while plugged in."));
        opts.Controls.Add(Check("Always on top", cfg.AlwaysOnTop, x => cfg.AlwaysOnTop = x, "Keep the phone window above other windows."));
        opts.Controls.Add(Check("Full screen", cfg.Fullscreen, x => cfg.Fullscreen = x, "Open the mirror full screen (Alt+F toggles)."));
        opts.Controls.Add(Check("Record to video", cfg.Record, x => cfg.Record = x, "Save an .mp4 to Videos\\Phone Mirror."));
        var sizeLabel = new Label { Text = "Quality:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = Theme.Muted };
        var size = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            Width = 170,
            Margin = new Padding(0, 2, 0, 6),
        };
        foreach (var s in Sizes) size.Items.Add(s.Label);
        size.SelectedIndex = Math.Max(0, Array.FindIndex(Sizes, s => s.Value == cfg.MaxSize));
        size.SelectedIndexChanged += (_, _) => { cfg.MaxSize = Sizes[size.SelectedIndex].Value; cfg.Save(); };
        new ToolTip().SetToolTip(size, "Auto = sharp over USB, smooth and low-lag over Wi-Fi.");
        opts.Controls.Add(sizeLabel);
        opts.Controls.Add(size);
        var lookLabel = new Label { Text = "Look:", AutoSize = true, Margin = new Padding(14, 6, 4, 0), ForeColor = Theme.Muted };
        var look = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            Width = 150,
            Margin = new Padding(0, 2, 0, 6),
        };
        foreach (var l in Looks) look.Items.Add(l.Label);
        look.SelectedIndex = Math.Max(0, Array.FindIndex(Looks, l => l.Value == cfg.FrameStyle));
        look.SelectedIndexChanged += (_, _) => { cfg.FrameStyle = Looks[look.SelectedIndex].Value; cfg.Save(); };
        new ToolTip().SetToolTip(look, "Show the mirror inside a phone-shaped frame, or as a plain window.");
        opts.Controls.Add(lookLabel);
        opts.Controls.Add(look);
        Row(opts);

        // ---- tools ----
        var toolsRow = Flow();
        toolsRow.Margin = new Padding(0, 8, 0, 0);
        var shotBtn = Theme.Button("Screenshot");
        shotBtn.Click += async (_, _) => await Screenshot();
        var recBtn = Theme.Button("Open recordings");
        recBtn.Click += (_, _) => OpenFolder(RecordDir);
        var picBtn = Theme.Button("Open screenshots");
        picBtn.Click += (_, _) => OpenFolder(ShotDir);
        var updBtn = Theme.Button("Update engine");
        updBtn.Click += async (_, _) => await UpdateEngine();
        toolsRow.Controls.AddRange(new Control[] { shotBtn, recBtn, picBtn, updBtn });
        needsEngine.Add(shotBtn);
        Row(toolsRow);

        // ---- activity log ----
        Row(Theme.Heading("Activity"));
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.BackColor = Theme.Panel;
        log.ForeColor = Theme.Muted;
        log.BorderStyle = BorderStyle.None;
        log.Font = new Font("Consolas", 9f);
        Row(log, SizeType.Percent, 100);

        // ---- status bar ----
        var bar = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        status.AutoSize = true;
        status.ForeColor = Theme.Muted;
        status.Margin = new Padding(0, 3, 0, 0);
        progress.Dock = DockStyle.Fill;
        progress.Visible = false;
        progress.Height = 14;
        bar.Controls.Add(status, 0, 0);
        bar.Controls.Add(progress, 1, 0);
        Row(bar);

        Controls.Add(root);

        // ---- tray icon ----
        tray.Icon = Icon;
        tray.Text = "Phone Mirror";
        tray.Visible = true;
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open Phone Mirror", null, (_, _) => ShowWindow());
        trayMenu.Items.Add("Mirror my phone", null, (_, _) => StartMirror());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Quit", null, (_, _) => { reallyQuit = true; Close(); });
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowWindow();

        if (Program.ShowSignal != null)
            ThreadPool.RegisterWaitForSingleObject(Program.ShowSignal, (_, _) => Ui(ShowWindow), null, -1, false);

        poll.Tick += async (_, _) => { await RefreshDevices(); await ReconnectWifi(); };
        foreach (var c in needsEngine) c.Enabled = false;
        mirrorBtn.Enabled = false;
        ShowPlaceholder("Getting things ready…");
    }

    // ================= startup / shutdown =================

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Program.Background) { Hide(); Opacity = 1; }
        if (OfferInstall()) return;
        if (cfg.StartWithWindows) Installer.SetStartWithWindows(true); // keep the path current

        try
        {
            if (!Tools.Locate())
            {
                Log("First run: downloading the mirroring engine (scrcpy, free & open source)…");
                await InstallEngine();
            }
            else Log($"Mirroring engine ready (scrcpy {Tools.InstalledVersion}).");

            await Tools.AdbAsync(15000, "start-server");
            engineReady = true;
            foreach (var c in needsEngine) c.Enabled = true;

            allowAuto = !Program.Background;
            await RefreshDevices();
            allowAuto = true;
            _ = Task.Run(TrackDevicesLoop);
            poll.Start();
            if (devices.Count == 0) Log("Plug your phone in with a USB cable to begin. First time? Click \"Phone setup help\".");
            _ = ReconnectWifi();
        }
        catch (Exception ex)
        {
            Log("Setup failed: " + ex.Message);
            Log("Check your internet connection, then click \"Update engine\".");
            ShowPlaceholder("Mirroring engine not installed");
        }
        _ = CheckForUpdate();
    }

    /// <summary>First run from Downloads: offer to add it to the Start menu and desktop.</summary>
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
            Log("Couldn't install: " + ex.Message);
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
                    "It's down here in the tray, ready for when you plug in your phone. Right-click › Quit to close it.", ToolTipIcon.Info);
            }
            return;
        }
        closing = true;
        poll.Stop();
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

    // ================= engine =================

    async Task InstallEngine()
    {
        progress.Visible = true;
        progress.Value = 0;
        try
        {
            var tag = await Tools.InstallAsync((msg, pct) =>
            {
                status.Text = msg;
                progress.Value = Math.Clamp(pct, 0, 100);
            });
            Log($"Installed scrcpy {tag}.");
        }
        finally
        {
            progress.Visible = false;
            status.Text = "";
        }
    }

    async Task UpdateEngine()
    {
        if (mirrorsOpen > 0)
        {
            MessageBox.Show(this, "Close all phone windows before updating.", "Phone Mirror", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        poll.Stop();
        engineReady = false;
        try { tracker?.Kill(); } catch { }
        try
        {
            Log("Updating the mirroring engine…");
            await InstallEngine();
            await Tools.AdbAsync(15000, "start-server");
            engineReady = true;
            foreach (var c in needsEngine) c.Enabled = true;
            deviceSignature = "";
        }
        catch (Exception ex) { Log("Update failed: " + ex.Message); }
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
            updateLabel.Text = $"A new version of Phone Mirror is ready ({u.Value.Version.Major}.{u.Value.Version.Minor}.{u.Value.Version.Build}).";
            updateBar.Visible = true;
        }
        catch { /* offline: ignore */ }
    }

    async Task ApplyUpdate()
    {
        if (updateUrl == null) return;
        updateBar.Enabled = false;
        progress.Visible = true;
        try
        {
            Log("Downloading update…");
            await Updater.ApplyAsync(updateUrl, pct => { progress.Value = Math.Clamp(pct, 0, 100); status.Text = $"Downloading update… {pct}%"; });
            reallyQuit = true;
            Close();
        }
        catch (Exception ex)
        {
            Log("Update failed: " + ex.Message);
            updateBar.Enabled = true;
            progress.Visible = false;
            status.Text = "";
        }
    }

    // ================= devices =================

    /// <summary>Instant plug/unplug detection: adb streams an event whenever the device list changes.</summary>
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
        catch (Exception ex) { Log("Couldn't check for phones: " + ex.Message); }
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
            if (d.State == "unauthorized") Log($"{d.Model}: unlock the phone and tap \"Allow\" on the USB debugging prompt.");
            else if (d.Ready) Log($"{d.Model} connected over {d.Connection}.");
        }
        foreach (var d in prev.Where(o => !now.Any(n => n.Serial == o.Serial)))
            Log($"{d.Model} ({d.Connection}) disconnected.");

        deviceSignature = sig;
        devices = now;
        RenderList();

        // Plugged in over USB → go wireless-ready, then open the mirror.
        var usb = newlyReady.FirstOrDefault(d => !d.Wireless);
        if (usb != null && allowAuto) await OnUsbPhoneArrived(usb);
    }

    void RenderList()
    {
        var selected = Selected?.Serial;
        list.BeginUpdate();
        list.Items.Clear();
        if (devices.Count == 0)
        {
            ShowPlaceholder("No phone connected — plug in with USB");
        }
        else
        {
            foreach (var d in devices)
            {
                var (text, color) = d.State switch
                {
                    "device" => ("Ready", Theme.Good),
                    "unauthorized" => ("Tap \"Allow\" on your phone", Theme.Warn),
                    "offline" => ("Offline — reconnect", Theme.Bad),
                    _ => (d.State, Theme.Warn),
                };
                var item = new ListViewItem(d.Model) { Tag = d, UseItemStyleForSubItems = false, ForeColor = Theme.Text, BackColor = Theme.Panel };
                item.SubItems.Add(d.Connection, Theme.Muted, Theme.Panel, list.Font);
                item.SubItems.Add(text, color, Theme.Panel, list.Font);
                list.Items.Add(item);
            }
            var items = list.Items.Cast<ListViewItem>().ToList();
            var pick = items.FirstOrDefault(i => (i.Tag as Device)?.Serial == selected)
                       ?? items.FirstOrDefault(i => i.Tag is Device { Ready: true, Wireless: false })
                       ?? items.FirstOrDefault(i => (i.Tag as Device)?.Ready == true)
                       ?? items[0];
            pick.Selected = true;
        }
        list.EndUpdate();
        FitColumns();
        UpdateButtons();
    }

    async Task OnUsbPhoneArrived(Device usb)
    {
        var target = usb;
        if (cfg.AutoWifi && !wifiDone.Contains(usb.Serial))
        {
            wifiDone.Add(usb.Serial);
            if (await EnableWifi(usb, quiet: true))
                target = await WaitReady(usb.Serial) ?? usb;
        }
        if (cfg.AutoMirror && mirrorsOpen == 0)
        {
            if (Program.Background && !Visible) tray.ShowBalloonTip(2500, "Phone Mirror", $"Opening {target.Model}…", ToolTipIcon.None);
            StartMirror(target);
        }
    }

    static async Task<Device?> WaitReady(string serial)
    {
        for (int i = 0; i < 12; i++)
        {
            var d = (await Tools.DevicesAsync()).FirstOrDefault(x => x.Serial == serial && x.Ready);
            if (d != null) return d;
            await Task.Delay(600);
        }
        return null;
    }

    void ShowPlaceholder(string message)
    {
        list.Items.Clear();
        var item = new ListViewItem(message) { ForeColor = Theme.Muted, BackColor = Theme.Panel };
        item.SubItems.Add("");
        item.SubItems.Add("");
        list.Items.Add(item);
        UpdateButtons();
    }

    Device? Selected =>
        list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is Device d
            ? d
            : devices.FirstOrDefault(x => x.Ready && !x.Wireless) ?? devices.FirstOrDefault(x => x.Ready);

    void UpdateButtons()
    {
        var d = Selected;
        mirrorBtn.Enabled = engineReady && d is { Ready: true };
        mirrorBtn.BackColor = mirrorBtn.Enabled ? Theme.Accent : Theme.Panel2;
        mirrorBtn.Text = d is { Ready: true } ? $"▶   Mirror {d.Model}" : "▶   Start Mirroring";
    }

    void FitColumns()
    {
        if (list.Columns.Count < 3) return;
        var w = list.ClientSize.Width;
        list.Columns[1].Width = 100;
        list.Columns[2].Width = Math.Max(160, (int)(w * 0.36));
        list.Columns[0].Width = Math.Max(120, w - list.Columns[1].Width - list.Columns[2].Width - 2);
    }

    // ================= mirroring =================

    void StartMirror() => StartMirror(null);

    void StartMirror(Device? target)
    {
        if (!engineReady || Tools.Scrcpy == null) return;
        var d = target ?? Selected;
        if (d == null) { Log("No phone connected yet. Plug it in with USB, or add it wirelessly."); return; }
        if (!d.Ready)
        {
            Log(d.State == "unauthorized"
                ? "Unlock your phone and tap \"Allow\" on the USB debugging prompt first."
                : "That phone is offline. Unplug and replug it (or reconnect Wi-Fi).");
            return;
        }

        int size = cfg.MaxSize;
        string bitrate = d.Wireless ? "6M" : "8M";
        if (size < 0)
        {
            size = d.Wireless ? 1280 : 1920;   // Auto: lower over Wi-Fi = less lag
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
        if (framed)
        {
            // borderless and off-screen until it's placed inside the phone frame
            args.Add("--window-borderless");
            args.Add("--window-x=-20000");
            args.Add("--window-y=-20000");
        }
        else
        {
            if (cfg.AlwaysOnTop) args.Add("--always-on-top");
            if (cfg.Fullscreen) args.Add("--fullscreen");
        }
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
        PhoneFrameForm? frame = null;
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (line.Contains("ERROR") || line.Contains("WARN")) Ui(() => Log("  " + line.Trim()));
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => Ui(() =>
        {
            mirrorsOpen--;
            int code = 0;
            try { code = p.ExitCode; } catch { }
            frame?.ProcessExited();
            p.Dispose();
            if (rec != null && File.Exists(rec))
            {
                Log($"Recording saved: {rec}");
                try { Process.Start("explorer.exe", $"/select,\"{rec}\""); } catch { }
            }
            if (code == 2 && !d.Wireless && cfg.AutoWifi && !closing)
            {
                _ = Handoff(d);   // cable pulled → continue over Wi-Fi
            }
            else if (code != 0 && (DateTime.Now - started).TotalSeconds < 15)
            {
                Log($"Mirror for {d.Model} couldn't start.");
                Log("  Tip: unlock the phone, check for an \"Allow\" prompt, or try a lower Quality setting.");
            }
            else Log($"Closed mirror for {d.Model}.");
        });

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            mirrorsOpen++;
            Log($"Opening {d.Model} ({d.Connection})…{(rec != null ? " recording." : "")}");
            if (framed)
            {
                frame = new PhoneFrameForm(p, d, cfg.FrameStyle, cfg.AlwaysOnTop) { ScreenshotRequested = dev => Screenshot(dev) };
                _ = AttachFrame(frame, p);
            }
        }
        catch (Exception ex) { Log("Couldn't launch the mirror: " + ex.Message); }
    }

    async Task AttachFrame(PhoneFrameForm frame, Process p)
    {
        bool ok = false;
        try { ok = await frame.AttachAsync(Screen.FromControl(this)); }
        catch (Exception ex) { Log("Phone frame error: " + ex.Message); }
        if (!ok)
        {
            frame.Dispose();
            // fall back: bring the plain scrcpy window on screen
            try
            {
                p.Refresh();
                if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero)
                    MoveWindow(p.MainWindowHandle, 100, 60, 420, 860, true);
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);

    async Task Handoff(Device usb)
    {
        Log($"{usb.Model} unplugged — switching to Wi-Fi…");
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
        Log("Couldn't switch to Wi-Fi. Is the phone on the same Wi-Fi as this laptop?");
    }

    // ================= Wi-Fi =================

    async Task<bool> EnableWifi(Device usb, bool quiet)
    {
        var ip = await PhoneWifiIp(usb.Serial);
        if (ip == null)
        {
            if (!quiet) Log("Couldn't find the phone's Wi-Fi address. Is Wi-Fi turned on?");
            return false;
        }

        bool restarted = false;
        var (_, port) = await Tools.AdbAsync("-s", usb.Serial, "shell", "getprop", "service.adb.tcp.port");
        if (port.Trim() != "5555")
        {
            await Tools.AdbAsync("-s", usb.Serial, "tcpip", "5555");
            restarted = true;
            await Task.Delay(2000);
        }
        var (_, o) = await Tools.AdbAsync(8000, "connect", $"{ip}:5555");
        if (o.Contains("connected to"))
        {
            cfg.LastIp = ip;
            cfg.Save();
            Log($"Wi-Fi ready for {usb.Model} ({ip}) — you can unplug anytime.");
            return restarted;
        }
        if (!quiet) Log("Wi-Fi connection failed: " + FirstLine(o));
        return restarted;
    }

    async Task ManualEnableWifi()
    {
        var usb = Selected is { Wireless: false, Ready: true } s ? s : devices.FirstOrDefault(d => !d.Wireless && d.Ready);
        if (usb == null)
        {
            MessageBox.Show(this,
                "Plug your phone in with a USB cable first (and tap \"Allow\" on the phone).\n\n" +
                "Both the phone and laptop need to be on the same Wi-Fi network.",
                "Enable Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Log($"Setting up Wi-Fi for {usb.Model}…");
        wifiDone.Add(usb.Serial);
        await EnableWifi(usb, quiet: false);
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

    /// <summary>Quietly re-attaches the last Wi-Fi phone when it's back on the network.</summary>
    async Task ReconnectWifi()
    {
        if (!engineReady || cfg.LastIp == "" || (DateTime.Now - lastReconnect).TotalSeconds < 15) return;
        if (devices.Any(d => d.Wireless && d.Serial.StartsWith(cfg.LastIp + ":"))) return;
        lastReconnect = DateTime.Now;
        var (_, o) = await Tools.AdbAsync(4000, "connect", WithPort(cfg.LastIp));
        if (o.Contains("connected to")) await RefreshDevices();
    }

    async Task PairQr()
    {
        using var f = new QrPairForm();
        var r = f.ShowDialog(this);
        if (r == DialogResult.Retry) { await PairWithCode(); return; }
        if (r == DialogResult.OK)
        {
            Log("Phone added wirelessly.");
            await RefreshDevices();
            var d = devices.FirstOrDefault(x => x.Serial == f.ConnectedTo);
            if (d != null && cfg.AutoMirror && mirrorsOpen == 0) StartMirror(d);
        }
    }

    async Task ConnectWifi()
    {
        var res = Prompt.Show(this, "Connect by IP",
            "Enter your phone's IP address (and port, if Wireless debugging shows one).\n" +
            "Find it on the phone under Settings › About phone › Status, or on the Wireless debugging screen.",
            ("Phone IP address", cfg.LastIp));
        if (res == null || res[0] == "") return;
        var target = WithPort(res[0]);
        Log($"Connecting to {target}…");
        var (_, o) = await Tools.AdbAsync(10000, "connect", target);
        if (o.Contains("connected to"))
        {
            if (target.EndsWith(":5555")) { cfg.LastIp = target[..^5]; cfg.Save(); }
            Log("Connected.");
            await RefreshDevices();
        }
        else
        {
            Log("Couldn't reach the phone: " + FirstLine(o));
            Log("  Try \"Add phone wirelessly (QR code)\" instead, or plug in with USB once.");
        }
    }

    async Task PairWithCode()
    {
        var res = Prompt.Show(this, "Pair with a code (Android 11+)",
            "On your phone: Settings › Developer options › Wireless debugging › turn ON.\n" +
            "Tap \"Pair device with pairing code\" and type what it shows below.",
            ("IP address & port shown in the popup (e.g. 192.168.1.20:37099)", ""),
            ("Wi-Fi pairing code (6 digits)", ""));
        if (res == null || res[0] == "" || res[1] == "") return;

        Log($"Pairing with {res[0]}…");
        var (_, o) = await Tools.AdbAsync(20000, "pair", res[0], res[1]);
        if (!o.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            Log("Pairing failed: " + FirstLine(o));
            Log("  Codes expire quickly — open a fresh pairing code and try again.");
            return;
        }
        Log("Paired! Waiting for the phone to connect…");
        var ip = res[0].Split(':')[0];
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            var (_, s) = await Tools.AdbAsync(5000, "mdns", "services");
            var m = s.Split('\n').Where(l => l.Contains("_adb-tls-connect") && l.Contains(ip + ":"))
                     .Select(l => Regex.Match(l, @"\d+\.\d+\.\d+\.\d+:\d+")).FirstOrDefault(x => x.Success);
            if (m != null) { await Tools.AdbAsync(8000, "connect", m.Value); break; }
        }
        await RefreshDevices();
    }

    async Task Disconnect()
    {
        var d = Selected;
        if (d == null || !d.Wireless) { Log("Select a Wi-Fi phone to disconnect."); return; }
        await Tools.AdbAsync("disconnect", d.Serial);
        if (cfg.LastIp != "" && d.Serial.StartsWith(cfg.LastIp + ":")) { cfg.LastIp = ""; cfg.Save(); }
        Log($"Disconnected {d.Model}.");
        await RefreshDevices();
    }

    // ================= extras =================

    Task Screenshot() => Screenshot(null);

    async Task Screenshot(Device? target)
    {
        var d = target ?? Selected;
        if (d is not { Ready: true }) { Log("Connect a phone first."); return; }
        Directory.CreateDirectory(ShotDir);
        var file = Path.Combine(ShotDir, $"{Safe(d.Model)} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        Log("Taking screenshot…");
        if (await Tools.ScreenshotAsync(d.Serial, file))
        {
            Log($"Saved {file}");
            try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
        }
        else
        {
            try { File.Delete(file); } catch { }
            Log("Screenshot failed. Is the phone unlocked?");
        }
    }

    void ShowHelp()
    {
        MessageBox.Show(this,
            "ONE-TIME PHONE SETUP\n" +
            "1. On your phone open Settings › About phone.\n" +
            "    (Samsung: About phone › Software information.)\n" +
            "2. Tap \"Build number\" 7 times until it says you're a developer.\n" +
            "3. Go to Settings › System › Developer options and turn on \"USB debugging\".\n" +
            "4. Plug the phone into this laptop. Unlock it and tap \"Allow\"\n" +
            "    (tick \"Always allow from this computer\").\n" +
            "5. That's it. The phone opens here automatically.\n\n" +
            "WIRELESS\n" +
            "• Plug in once and Phone Mirror turns on Wi-Fi mode for you. Unplug and the\n" +
            "   mirror carries on over Wi-Fi. (Needs one plug-in again after the phone restarts.)\n" +
            "• No cable at all (Android 11+): \"Add phone wirelessly (QR code)\".\n\n" +
            "IN THE PHONE WINDOW\n" +
            "• Click / type / scroll to use the phone\n" +
            "• Right-click = Back,  Middle-click = Home\n" +
            "• Alt+F full screen,  Alt+R rotate,  Alt+O phone screen off\n" +
            "• Drag an .apk onto the window to install it, or any other file to copy it to the phone's Download folder",
            "Phone Mirror — Help", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================= helpers =================

    static FlowLayoutPanel Flow() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = true,
        Margin = new Padding(0),
    };

    CheckBox Check(string text, bool value, Action<bool> set, string tip)
    {
        var c = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 4, 18, 6),
            Cursor = Cursors.Hand,
        };
        c.CheckedChanged += (_, _) => { set(c.Checked); cfg.Save(); };
        new ToolTip().SetToolTip(c, tip);
        return c;
    }

    void Log(string msg)
    {
        if (log.TextLength > 60000) log.Text = log.Text[^30000..];
        log.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\r\n");
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

    static string WithPort(string ip) => ip.Contains(':') ? ip : ip + ":5555";
    static string FirstLine(string s) => s.Split('\n').FirstOrDefault(l => l.Trim() != "")?.Trim() ?? "no response";
    static string Safe(string s) => string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
