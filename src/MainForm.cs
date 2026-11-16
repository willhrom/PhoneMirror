using System.Diagnostics;
using System.Reflection;
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
    readonly System.Windows.Forms.Timer poll = new() { Interval = 2500 };
    readonly List<Control> needsEngine = new();

    List<Device> devices = new();
    string deviceSignature = "";
    bool refreshing, engineReady;
    int mirrorsOpen;

    static string RecordDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Phone Mirror");
    static string ShotDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Phone Mirror");

    static readonly (string Label, int Value)[] Sizes =
    {
        ("Full resolution", 0), ("1920 (sharp)", 1920), ("1600", 1600), ("1280 (smooth)", 1280), ("1024", 1024), ("800 (slow Wi-Fi)", 800),
    };

    public MainForm()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        Text = $"Phone Mirror {ver?.Major}.{ver?.Minor}.{ver?.Build}";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 720);
        MinimumSize = new Size(580, 620);
        StartPosition = FormStartPosition.CenterScreen;

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
        titleBox.Controls.Add(new Label { Text = "See and control your Android phone on this laptop", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(2, 0, 0, 0) });
        var help = Theme.Button("Phone setup help");
        help.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        help.Margin = new Padding(0, 10, 0, 0);
        help.Click += (_, _) => ShowHelp();
        header.Controls.Add(titleBox, 0, 0);
        header.Controls.Add(help, 1, 0);
        Row(header);

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
        list.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Theme.Panel2);
            e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, new Font("Segoe UI", 8.5f, FontStyle.Bold),
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
        menu.Items.Add("Disconnect (Wi-Fi)", null, async (_, _) => await Disconnect());
        list.ContextMenuStrip = menu;
        Row(list, SizeType.Absolute, 140);

        var connectRow = Flow();
        var wifiBtn = Theme.Button("Connect over Wi-Fi…");
        wifiBtn.Click += async (_, _) => await ConnectWifi();
        var setupBtn = Theme.Button("Set up Wi-Fi (via USB)");
        setupBtn.Click += async (_, _) => await SetupWifi();
        var pairBtn = Theme.Button("Pair without cable…");
        pairBtn.Click += async (_, _) => await Pair();
        connectRow.Controls.AddRange(new Control[] { wifiBtn, setupBtn, pairBtn });
        connectRow.Margin = new Padding(0, 10, 0, 0);
        needsEngine.AddRange(new Control[] { wifiBtn, setupBtn, pairBtn });
        Row(connectRow);

        // ---- big button ----
        mirrorBtn = Theme.Button("▶   Start Mirroring", accent: true);
        mirrorBtn.Margin = new Padding(0, 6, 0, 4);
        mirrorBtn.Click += (_, _) => StartMirror();
        Row(mirrorBtn, SizeType.Absolute, 62);

        // ---- options ----
        Row(Theme.Heading("Options"));
        var opts = Flow();
        opts.Controls.Add(Check("Turn phone screen off", cfg.ScreenOff, v => cfg.ScreenOff = v,
            "The phone's display goes dark to save battery, but it keeps working here."));
        opts.Controls.Add(Check("Phone audio", cfg.Audio, v => cfg.Audio = v, "Play the phone's sound on the laptop (Android 11+)."));
        opts.Controls.Add(Check("Keep phone awake", cfg.StayAwake, v => cfg.StayAwake = v, "Stop the phone from sleeping while plugged in."));
        opts.Controls.Add(Check("Always on top", cfg.AlwaysOnTop, v => cfg.AlwaysOnTop = v, "Keep the phone window above other windows."));
        opts.Controls.Add(Check("Full screen", cfg.Fullscreen, v => cfg.Fullscreen = v, "Open the mirror full screen (Alt+F toggles)."));
        opts.Controls.Add(Check("Record to video", cfg.Record, v => cfg.Record = v, "Save an .mp4 to Videos\\Phone Mirror."));
        var sizeLabel = new Label { Text = "Quality:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), ForeColor = Theme.Muted };
        var size = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Panel2,
            ForeColor = Theme.Text,
            Width = 150,
            Margin = new Padding(0, 2, 0, 6),
        };
        foreach (var s in Sizes) size.Items.Add(s.Label);
        size.SelectedIndex = Math.Max(0, Array.FindIndex(Sizes, s => s.Value == cfg.MaxSize));
        size.SelectedIndexChanged += (_, _) => { cfg.MaxSize = Sizes[size.SelectedIndex].Value; cfg.Save(); };
        opts.Controls.Add(sizeLabel);
        opts.Controls.Add(size);
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

        poll.Tick += async (_, _) => await RefreshDevices();
        foreach (var c in needsEngine) c.Enabled = false;
        mirrorBtn.Enabled = false;
        ShowPlaceholder("Getting things ready…");
    }

    // ================= startup =================

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
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

            if (!string.IsNullOrEmpty(cfg.LastIp))
            {
                var (_, o) = await Tools.AdbAsync(6000, "connect", WithPort(cfg.LastIp));
                if (o.Contains("connected to")) Log($"Reconnected over Wi-Fi to {cfg.LastIp}.");
            }
            await RefreshDevices();
            poll.Start();
            if (devices.Count == 0) Log("Plug your phone in with a USB cable to begin. First time? Click \"Phone setup help\".");
        }
        catch (Exception ex)
        {
            Log("Setup failed: " + ex.Message);
            Log("Check your internet connection, then click \"Update engine\".");
            ShowPlaceholder("Mirroring engine not installed");
        }
    }

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
        try
        {
            Log("Updating the mirroring engine…");
            await InstallEngine();
            await Tools.AdbAsync(15000, "start-server");
            engineReady = true;
            foreach (var c in needsEngine) c.Enabled = true;
        }
        catch (Exception ex) { Log("Update failed: " + ex.Message); }
        await RefreshDevices();
        poll.Start();
    }

    // ================= devices =================

    async Task RefreshDevices()
    {
        if (refreshing || !engineReady) return;
        refreshing = true;
        try
        {
            var now = await Tools.DevicesAsync();
            var sig = string.Join("|", now.Select(d => d.Serial + d.State + d.Model));
            if (sig == deviceSignature) return;

            foreach (var d in now.Where(d => !devices.Any(o => o.Serial == d.Serial && o.State == d.State)))
            {
                if (d.State == "unauthorized") Log($"{d.Model}: unlock the phone and tap \"Allow\" on the USB debugging prompt.");
                else if (d.Ready) Log($"{d.Model} connected over {d.Connection}.");
            }
            foreach (var d in devices.Where(o => !now.Any(n => n.Serial == o.Serial)))
                Log($"{d.Model} ({d.Connection}) disconnected.");

            deviceSignature = sig;
            devices = now;
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
                var pick = list.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as Device)?.Serial == selected)
                           ?? list.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as Device)?.Ready == true)
                           ?? list.Items[0];
                pick.Selected = true;
            }
            list.EndUpdate();
            FitColumns();
            UpdateButtons();
        }
        catch (Exception ex) { Log("Couldn't check for phones: " + ex.Message); }
        finally { refreshing = false; }
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
        list.SelectedItems.Count > 0 && list.SelectedItems[0].Tag is Device d ? d : devices.FirstOrDefault(x => x.Ready);

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

    void StartMirror()
    {
        if (!engineReady || Tools.Scrcpy == null) return;
        var d = Selected;
        if (d == null) { Log("No phone connected yet. Plug it in with USB, or connect over Wi-Fi."); return; }
        if (!d.Ready)
        {
            Log(d.State == "unauthorized"
                ? "Unlock your phone and tap \"Allow\" on the USB debugging prompt first."
                : "That phone is offline. Unplug and replug it (or reconnect Wi-Fi).");
            return;
        }

        var args = new List<string> { $"--serial={d.Serial}", $"--window-title={d.Model} — Phone Mirror" };
        if (cfg.StayAwake) args.Add("--stay-awake");
        if (cfg.ScreenOff) args.Add("--turn-screen-off");
        if (!cfg.Audio) args.Add("--no-audio");
        if (cfg.AlwaysOnTop) args.Add("--always-on-top");
        if (cfg.Fullscreen) args.Add("--fullscreen");
        if (cfg.MaxSize > 0) args.Add($"--max-size={cfg.MaxSize}");
        if (d.Wireless) args.Add("--video-bit-rate=6M");
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
        foreach (var a in args) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = DateTime.Now;
        var lastLines = new List<string>();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (lastLines) { lastLines.Add(line); if (lastLines.Count > 6) lastLines.RemoveAt(0); }
            if (line.Contains("ERROR") || line.Contains("WARN")) Ui(() => Log("  " + line.Trim()));
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => Ui(() =>
        {
            mirrorsOpen--;
            int code = 0;
            try { code = p.ExitCode; } catch { }
            if (code != 0 && (DateTime.Now - started).TotalSeconds < 15)
            {
                Log($"Mirror for {d.Model} couldn't start.");
                Log("  Tip: unlock the phone, check for an \"Allow\" prompt, or try a lower Quality setting.");
            }
            else Log($"Closed mirror for {d.Model}.");
            if (rec != null && File.Exists(rec))
            {
                Log($"Recording saved: {rec}");
                try { Process.Start("explorer.exe", $"/select,\"{rec}\""); } catch { }
            }
            p.Dispose();
        });

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            mirrorsOpen++;
            Log($"Opening {d.Model} ({d.Connection})…{(rec != null ? " recording." : "")}");
        }
        catch (Exception ex) { Log("Couldn't launch the mirror: " + ex.Message); }
    }

    // ================= Wi-Fi =================

    async Task SetupWifi()
    {
        var usb = (Selected is { Wireless: false, Ready: true } s ? s : null) ?? devices.FirstOrDefault(d => !d.Wireless && d.Ready);
        if (usb == null)
        {
            MessageBox.Show(this,
                "Plug your phone into the laptop with a USB cable first (and tap \"Allow\" on the phone).\n\n" +
                "Both the phone and laptop need to be on the same Wi-Fi network.",
                "Set up Wi-Fi", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log($"Setting up Wi-Fi for {usb.Model}…");
        var ip = await PhoneWifiIp(usb.Serial);
        if (ip == null) { Log("Couldn't find the phone's Wi-Fi address. Is Wi-Fi turned on?"); return; }

        await Tools.AdbAsync("-s", usb.Serial, "tcpip", "5555");
        await Task.Delay(2500);
        var (_, o) = await Tools.AdbAsync(10000, "connect", $"{ip}:5555");
        if (o.Contains("connected to"))
        {
            cfg.LastIp = ip;
            cfg.Save();
            Log($"Connected over Wi-Fi ({ip}). You can unplug the cable now.");
            Log("  Phone Mirror will reconnect automatically next time (until the phone restarts).");
            await RefreshDevices();
        }
        else Log("Wi-Fi connection failed: " + FirstLine(o));
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

    async Task ConnectWifi()
    {
        var res = Prompt.Show(this, "Connect over Wi-Fi",
            "Enter your phone's IP address. Find it on the phone under Settings › About phone › Status (or Wi-Fi details).\n\n" +
            "This only works after \"Set up Wi-Fi (via USB)\" since the phone last restarted. Otherwise use \"Pair without cable\".",
            ("Phone IP address", cfg.LastIp));
        if (res == null || res[0] == "") return;
        var target = WithPort(res[0]);
        Log($"Connecting to {target}…");
        var (_, o) = await Tools.AdbAsync(10000, "connect", target);
        if (o.Contains("connected to"))
        {
            cfg.LastIp = res[0].Replace(":5555", "");
            cfg.Save();
            Log("Connected.");
            await RefreshDevices();
        }
        else
        {
            Log("Couldn't reach the phone: " + FirstLine(o));
            Log("  Plug in USB and use \"Set up Wi-Fi\", or use \"Pair without cable\" (Android 11+).");
        }
    }

    async Task Pair()
    {
        var res = Prompt.Show(this, "Pair without a cable (Android 11+)",
            "On your phone: Settings › Developer options › Wireless debugging › turn ON.\n" +
            "Tap \"Pair device with pairing code\" and type what it shows below.\n\n" +
            "The connect address is on the main Wireless debugging screen (it has a different port). Leave it blank to auto-detect.",
            ("Pairing IP address & port (from the popup, e.g. 192.168.1.20:37099)", ""),
            ("Wi-Fi pairing code (6 digits)", ""),
            ("Connect IP address & port (optional, e.g. 192.168.1.20:41235)", ""));
        if (res == null || res[0] == "" || res[1] == "") return;

        Log($"Pairing with {res[0]}…");
        var (_, o) = await Tools.AdbAsync(20000, "pair", res[0], res[1]);
        if (!o.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            Log("Pairing failed: " + FirstLine(o));
            Log("  Codes expire quickly — open a fresh pairing code and try again.");
            return;
        }
        Log("Paired!");
        if (res[2] != "")
        {
            var (_, c) = await Tools.AdbAsync(10000, "connect", res[2]);
            Log(c.Contains("connected to") ? "Connected over Wi-Fi." : "Connect failed: " + FirstLine(c));
        }
        else
        {
            Log("Waiting for the phone to appear…");
            await Task.Delay(3000);
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

    async Task Screenshot()
    {
        var d = Selected;
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
            "4. Plug the phone into this laptop with a data cable. Unlock it and tap \"Allow\"\n" +
            "    (tick \"Always allow from this computer\").\n" +
            "5. Your phone appears in the list. Click Start Mirroring.\n\n" +
            "WIRELESS\n" +
            "• Easiest: with the phone plugged in, click \"Set up Wi-Fi (via USB)\", then unplug.\n" +
            "   Phone Mirror reconnects on its own until the phone restarts.\n" +
            "• No cable at all (Android 11+): use \"Pair without cable\".\n\n" +
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
        if (IsDisposed || !IsHandleCreated) return;
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
