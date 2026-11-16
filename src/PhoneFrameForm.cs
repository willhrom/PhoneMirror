using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PhoneMirror;

/// <summary>
/// A borderless, phone-shaped window (bezel, camera, side buttons) that hosts the
/// scrcpy video window inside its "screen", plus a small emulator-style button strip.
/// </summary>
class PhoneFrameForm : Form
{
    const int Gap = 12, BarW = 42, BtnH = 34;

    readonly Process proc;
    readonly Device device;
    readonly Color bodyColor, edgeColor, buttonColor;
    readonly Panel screen = new() { BackColor = Color.Black };
    readonly Panel bar = new();
    readonly Button pinBtn;
    readonly System.Windows.Forms.Timer watch = new() { Interval = 250 };

    IntPtr child;
    double aspect = 9.0 / 19.5;
    bool processExited, closeRequested, layingOut;

    public Func<Device, Task>? ScreenshotRequested;

    int Bezel => Math.Clamp(screen.Width / 30, 8, 16);
    int OuterRadius => Math.Clamp(Math.Min(screen.Width, screen.Height) / 7, 22, 60);
    int ScreenRadius => Math.Max(10, OuterRadius - Bezel);
    int PhoneW => screen.Width + 2 * Bezel;
    int PhoneH => screen.Height + 2 * Bezel;

    public PhoneFrameForm(Process proc, Device device, string style, bool topMost)
    {
        this.proc = proc;
        this.device = device;
        (bodyColor, edgeColor, buttonColor) = style switch
        {
            "Silver" => (Color.FromArgb(214, 216, 221), Color.FromArgb(160, 163, 170), Color.FromArgb(186, 189, 195)),
            "Blue" => (Color.FromArgb(38, 58, 100), Color.FromArgb(84, 112, 168), Color.FromArgb(58, 82, 130)),
            _ => (Color.FromArgb(22, 22, 25), Color.FromArgb(70, 70, 78), Color.FromArgb(46, 46, 52)),
        };

        Text = $"{device.Model} — Phone Mirror";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = bodyColor;
        DoubleBuffered = true;
        TopMost = topMost;
        ShowInTaskbar = true;

        bar.BackColor = Color.FromArgb(30, 32, 38);
        var buttons = new (string Glyph, string Tip, Action Act)[]
        {
            ("", "Close", () => Close()),
            ("", "Minimize", () => WindowState = FormWindowState.Minimized),
            ("", "Keep on top", TogglePin),
            ("", "Screenshot", () => { _ = ScreenshotRequested?.Invoke(device); }),
            ("", "Power / lock", () => Key(26)),
            ("", "Volume up", () => Key(24)),
            ("", "Volume down", () => Key(25)),
            ("", "Back", () => Key(4)),
            ("", "Home", () => Key(3)),
            ("", "Recent apps", () => Key(187)),
        };
        var tips = new ToolTip();
        int y = 8;
        Button? pin = null;
        foreach (var (glyph, tip, act) in buttons)
        {
            var b = new Button
            {
                Text = glyph,
                Font = new Font("Segoe MDL2 Assets", 11f),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(220, 224, 232),
                BackColor = bar.BackColor,
                Bounds = new Rectangle(4, y, BarW - 8, BtnH - 2),
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 60, 70);
            b.Click += (_, _) => { act(); FocusPhone(); };
            tips.SetToolTip(b, tip);
            bar.Controls.Add(b);
            if (tip == "Keep on top") pin = b;
            y += BtnH;
        }
        bar.Height = y + 6;
        pinBtn = pin!;
        UpdatePin();

        Controls.Add(screen);
        Controls.Add(bar);
        watch.Tick += (_, _) => Watch();
    }

    // ================= embedding =================

    /// <summary>Waits for the scrcpy window, then pulls it inside the phone screen.</summary>
    public async Task<bool> AttachAsync(Screen display)
    {
        for (int i = 0; i < 100 && child == IntPtr.Zero; i++)
        {
            if (proc.HasExited) return false;
            proc.Refresh();
            child = proc.MainWindowHandle;
            if (child == IntPtr.Zero) await Task.Delay(150);
        }
        if (child == IntPtr.Zero) return false;

        GetClientRect(child, out var rc);
        int cw = Math.Max(1, rc.Right - rc.Left), ch = Math.Max(1, rc.Bottom - rc.Top);
        aspect = (double)cw / ch;

        // Size to fit the screen nicely
        var wa = display.WorkingArea;
        int maxH = (int)(wa.Height * 0.86), maxW = (int)(wa.Width * 0.7);
        int sh, sw;
        if (aspect < 1) { sh = Math.Min(maxH, Math.Max(ch, 560)); sw = (int)Math.Round(sh * aspect); }
        else { sw = Math.Min(maxW, Math.Max(cw, 720)); sh = (int)Math.Round(sw / aspect); }
        SetScreenSize(sw, sh);
        Location = new Point(wa.Right - Width - 60, wa.Top + (wa.Height - Height) / 2);

        // Turn the scrcpy window into a child of our screen panel
        var style = (long)GetWindowLongPtr(child, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU)) | WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(child, GWL_STYLE, (IntPtr)style);
        var ex = (long)GetWindowLongPtr(child, GWL_EXSTYLE);
        ex &= ~(WS_EX_APPWINDOW | WS_EX_TOPMOST);
        SetWindowLongPtr(child, GWL_EXSTYLE, (IntPtr)ex);
        SetParent(child, screen.Handle);
        FitChild();

        Show();
        Activate();
        FocusPhone();
        watch.Start();
        return true;
    }

    void FitChild()
    {
        if (child != IntPtr.Zero) MoveWindow(child, 0, 0, screen.Width, screen.Height, true);
    }

    void FocusPhone()
    {
        if (child != IntPtr.Zero) SetFocus(child);
    }

    /// <summary>scrcpy resizes its own window when the phone rotates — follow it.</summary>
    void Watch()
    {
        if (child == IntPtr.Zero || processExited || WindowState == FormWindowState.Minimized) return;
        if (!GetWindowRect(child, out var r)) return;
        int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
        if (cw <= 0 || ch <= 0 || (cw == screen.Width && ch == screen.Height)) return;
        double a = (double)cw / ch;
        if ((a > 1) != (aspect > 1))
        {
            aspect = a;
            int longSide = Math.Max(screen.Width, screen.Height);
            var wa = Screen.FromControl(this).WorkingArea;
            int sw, sh;
            if (aspect > 1) { sw = Math.Min(longSide, (int)(wa.Width * 0.8)); sh = (int)Math.Round(sw / aspect); }
            else { sh = Math.Min(longSide, (int)(wa.Height * 0.86)); sw = (int)Math.Round(sh * aspect); }
            var center = new Point(Left + PhoneW / 2, Top + PhoneH / 2);
            SetScreenSize(sw, sh);
            Location = new Point(
                Math.Clamp(center.X - PhoneW / 2, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
                Math.Clamp(center.Y - PhoneH / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        }
        FitChild();
    }

    // ================= layout & shape =================

    void SetScreenSize(int sw, int sh)
    {
        layingOut = true;
        screen.Size = new Size(Math.Max(sw, 120), Math.Max(sh, 120));
        ClientSize = new Size(PhoneW + Gap + BarW, Math.Max(PhoneH, bar.Height + 24));
        layingOut = false;
        ApplyLayout();
    }

    void ApplyLayout()
    {
        int b = Bezel;
        screen.Location = new Point(b, b);
        using (var sp = RoundRect(new Rectangle(0, 0, screen.Width, screen.Height), ScreenRadius))
            screen.Region = new Region(sp);
        bar.Bounds = new Rectangle(PhoneW + Gap, Math.Min(Math.Max(20, PhoneH / 8), Math.Max(0, ClientSize.Height - bar.Height)), BarW, bar.Height);
        using (var bp = RoundRect(new Rectangle(0, 0, bar.Width, bar.Height), 12))
            bar.Region = new Region(bp);

        var region = new Region(RoundRect(new Rectangle(0, 0, PhoneW, PhoneH), OuterRadius));
        foreach (var nub in SideButtons()) region.Union(nub);
        region.Union(bar.Bounds);
        Region = region;
        FitChild();
        Invalidate();
    }

    IEnumerable<Rectangle> SideButtons()
    {
        int x = PhoneW, h = PhoneH;
        yield return new Rectangle(x - 1, (int)(h * 0.20), 4, (int)(h * 0.07));  // power
        yield return new Rectangle(x - 1, (int)(h * 0.30), 4, (int)(h * 0.12));  // volume
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bodyBrush = new SolidBrush(bodyColor))
        using (var path = RoundRect(new Rectangle(0, 0, PhoneW, PhoneH), OuterRadius))
            g.FillPath(bodyBrush, path);
        using (var pen = new Pen(edgeColor, 2f))
        using (var path = RoundRect(new Rectangle(1, 1, PhoneW - 3, PhoneH - 3), OuterRadius - 1))
            g.DrawPath(pen, path);
        using (var nubBrush = new SolidBrush(buttonColor))
            foreach (var nub in SideButtons()) g.FillRectangle(nubBrush, nub);

        // camera in the top bezel
        int r = Math.Max(3, Bezel / 3);
        var cam = new Rectangle(PhoneW / 2 - r, Bezel / 2 - r, r * 2, r * 2);
        using (var camBrush = new SolidBrush(Color.FromArgb(8, 8, 10))) g.FillEllipse(camBrush, cam);
        using (var glint = new SolidBrush(Color.FromArgb(60, 70, 110)))
            g.FillEllipse(glint, cam.X + r / 2, cam.Y + r / 2, Math.Max(1, r / 2), Math.Max(1, r / 2));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (layingOut || WindowState == FormWindowState.Minimized) return;
        int sw = ClientSize.Width - Gap - BarW - 2 * Bezel;
        if (sw > 0 && sw != screen.Width)
        {
            layingOut = true;
            screen.Size = new Size(sw, (int)Math.Round(sw / aspect));
            layingOut = false;
        }
        ApplyLayout();
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ================= dragging & resizing =================

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214, HTCAPTION = 2, HTBOTTOMRIGHT = 17;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (p.X < PhoneW && p.Y < PhoneH)
            {
                bool corner = p.X > PhoneW - OuterRadius && p.Y > PhoneH - OuterRadius &&
                              (p.X > PhoneW - Bezel - 6 || p.Y > PhoneH - Bezel - 6);
                m.Result = (IntPtr)(corner ? HTBOTTOMRIGHT : HTCAPTION);
            }
            return;
        }
        if (m.Msg == WM_SIZING)
        {
            // keep the phone's proportions while resizing
            var rc = Marshal.PtrToStructure<RECT>(m.LParam);
            int extraW = Gap + BarW + 2 * Bezel;
            int sw = Math.Max(140, rc.Right - rc.Left - extraW);
            int sh = (int)Math.Round(sw / aspect);
            rc.Right = rc.Left + sw + extraW;
            rc.Bottom = rc.Top + Math.Max(sh + 2 * Bezel, bar.Height + 24);
            Marshal.StructureToPtr(rc, m.LParam, false);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    // ================= buttons =================

    void Key(int code) => _ = Tools.AdbAsync(5000, "-s", device.Serial, "shell", "input", "keyevent", code.ToString());

    void TogglePin()
    {
        TopMost = !TopMost;
        UpdatePin();
    }

    void UpdatePin() => pinBtn.ForeColor = TopMost ? Theme.Accent : Color.FromArgb(220, 224, 232);

    // ================= closing =================

    /// <summary>Called when scrcpy exits on its own (phone unplugged, etc.).</summary>
    public void ProcessExited()
    {
        processExited = true;
        watch.Stop();
        if (!IsDisposed) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!processExited && child != IntPtr.Zero)
        {
            // Ask scrcpy to quit cleanly (so recordings are finished properly); we close once it has.
            e.Cancel = true;
            if (!closeRequested)
            {
                closeRequested = true;
                PostMessage(child, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                _ = Task.Delay(3000).ContinueWith(_ => { try { if (!proc.HasExited) proc.Kill(); } catch { } });
            }
            return;
        }
        watch.Stop();
        base.OnFormClosing(e);
    }

    // ================= Win32 =================

    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_CHILD = 0x40000000L, WS_VISIBLE = 0x10000000L, WS_POPUP = 0x80000000L,
               WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, WS_SYSMENU = 0x00080000L,
               WS_EX_APPWINDOW = 0x00040000L, WS_EX_TOPMOST = 0x00000008L;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}
