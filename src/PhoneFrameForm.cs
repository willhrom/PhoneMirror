using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PhoneMirror;

/// <summary>
/// A borderless, phone-shaped window (bezel, camera, side buttons, button pill).
/// The scrcpy video window stays its own window but is "owned" by this frame and kept
/// glued over the frame's screen area, so it moves, minimizes and stacks with it.
/// Resize from any corner or with the +/− buttons; size and position are remembered per phone.
/// </summary>
class PhoneFrameForm : Form
{
    const int Gap = 10, BarW = 36, MinLong = 300;

    readonly Process proc;
    readonly Device device;
    readonly Color bodyColor, edgeColor, buttonColor, iconColor, hoverColor;
    readonly Panel bar = new();
    readonly List<Control> barItems = new();
    readonly Button pinBtn;
    readonly string stripMode;
    readonly int savedLong;
    readonly Point? savedPos;
    readonly System.Windows.Forms.Timer watch = new() { Interval = 200 };
    readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 120 };

    IntPtr child;
    double aspect = 9.0 / 19.5;
    Size screenSize = new(360, 780);
    Size regionSize;
    bool processExited, closeRequested, layingOut;
    DateTime lastInside = DateTime.MinValue;

    public Func<Device, Task>? ScreenshotRequested;
    public Action<string>? Log;
    /// <summary>Called with (long side of the screen, window position) after a resize or move.</summary>
    public Action<int, Point>? SaveWindow;

    int Bezel => Math.Clamp(screenSize.Width / 30, 6, 16);
    int OuterRadius => Math.Clamp(Math.Min(screenSize.Width, screenSize.Height) / 7, 16, 60);
    int ScreenRadius => Math.Max(8, OuterRadius - Bezel);
    int PhoneW => screenSize.Width + 2 * Bezel;
    int PhoneH => screenSize.Height + 2 * Bezel;
    Rectangle ScreenRect => new(Bezel, Bezel, screenSize.Width, screenSize.Height);
    int LongSide => Math.Max(screenSize.Width, screenSize.Height);

    public PhoneFrameForm(Process proc, Device device, string style, bool topMost, string stripMode, int savedLong, Point? savedPos)
    {
        this.proc = proc;
        this.device = device;
        this.stripMode = stripMode;
        this.savedLong = savedLong;
        this.savedPos = savedPos;
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

        // Slim pill in the phone's own color, grouped: window · size · phone keys · navigation
        bool light = style == "Silver";
        bar.BackColor = style switch
        {
            "Silver" => Color.FromArgb(226, 228, 232),
            "Blue" => Color.FromArgb(32, 50, 88),
            _ => Color.FromArgb(26, 26, 30),
        };
        iconColor = light ? Color.FromArgb(60, 64, 72) : Color.FromArgb(200, 205, 215);
        hoverColor = light ? Color.FromArgb(206, 209, 215) : ControlPaint.Light(bar.BackColor, 0.35f);
        var items = new (string Glyph, string Tip, Action? Act)[]
        {
            ("", "Close", () => Close()),
            ("", "Minimize", () => WindowState = FormWindowState.Minimized),
            ("", "Keep on top", TogglePin),
            ("", "", null),
            ("", "Bigger", () => Zoom(1.15)),
            ("", "Smaller", () => Zoom(1 / 1.15)),
            ("", "", null),
            ("", "Screenshot", () => { _ = ScreenshotRequested?.Invoke(device); }),
            ("", "Power / lock", () => Key(26)),
            ("", "Volume up", () => Key(24)),
            ("", "Volume down", () => Key(25)),
            ("", "", null),
            ("", "Back", () => Key(4)),
            ("", "Home", () => Key(3)),
            ("", "Recent apps", () => Key(187)),
        };
        var tips = new ToolTip();
        Button? pin = null;
        foreach (var (glyph, tip, act) in items)
        {
            if (act == null)
            {
                var sep = new Panel { BackColor = hoverColor, Height = 1, Tag = "sep" };
                barItems.Add(sep);
                bar.Controls.Add(sep);
                continue;
            }
            var b = new Button
            {
                Text = glyph,
                Font = new Font("Segoe MDL2 Assets", 9.5f),
                FlatStyle = FlatStyle.Flat,
                ForeColor = iconColor,
                BackColor = bar.BackColor,
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hoverColor;
            b.FlatAppearance.MouseDownBackColor = hoverColor;
            b.Click += (_, _) => { act(); FocusPhone(); };
            tips.SetToolTip(b, tip);
            barItems.Add(b);
            bar.Controls.Add(b);
            if (tip == "Keep on top") pin = b;
        }
        pinBtn = pin!;
        UpdatePin();
        bar.Visible = stripMode == "always";

        Controls.Add(bar);
        watch.Tick += (_, _) => Watch();
        hoverTimer.Tick += (_, _) => CheckHover();
        if (stripMode == "hover") hoverTimer.Start();
        LocationChanged += (_, _) => PlaceChild();
    }

    // ================= attaching =================

    public async Task<bool> AttachAsync(Screen display)
    {
        for (int i = 0; i < 120 && child == IntPtr.Zero; i++)
        {
            if (proc.HasExited) return false;
            proc.Refresh();
            child = proc.MainWindowHandle;
            if (child == IntPtr.Zero) await Task.Delay(150);
        }
        if (child == IntPtr.Zero) { Log?.Invoke("Phone frame: couldn't find the mirror window."); return false; }

        GetClientRect(child, out var rc);
        int cw = Math.Max(1, rc.Right - rc.Left), ch = Math.Max(1, rc.Bottom - rc.Top);
        aspect = (double)cw / ch;

        // Use the remembered position's screen if it's still connected.
        if (savedPos is Point sp && Screen.AllScreens.Any(s => s.WorkingArea.Contains(sp)))
            display = Screen.FromPoint(sp);
        var wa = display.WorkingArea;
        int wanted = savedLong > 0 ? savedLong : (aspect < 1 ? Math.Max(ch, 640) : Math.Max(cw, 760));
        SetScreenSize(SizeForLong(Math.Clamp(wanted, MinLong, MaxLong(wa))));
        if (savedPos is Point p && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p, new Size(PhoneW, PhoneH)))))
            Location = p;
        else
            Location = new Point(wa.Right - Width - 60, wa.Top + (wa.Height - Height) / 2);

        Show();

        ShowWindow(child, SW_HIDE);
        var ex = (long)GetWindowLongPtr(child, GWL_EXSTYLE);
        ex = (ex & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(child, GWL_EXSTYLE, (IntPtr)ex);
        SetWindowLongPtr(child, GWLP_HWNDPARENT, Handle);
        PlaceChild();
        ShowWindow(child, SW_SHOW);
        SetForegroundWindow(child);
        watch.Start();
        return true;
    }

    int MaxLong(Rectangle wa) => aspect < 1 ? (int)(wa.Height * 0.92) - 32 : (int)(wa.Width * 0.85);

    Size SizeForLong(int longSide) => aspect < 1
        ? new Size((int)Math.Round(longSide * aspect), longSide)
        : new Size(longSide, (int)Math.Round(longSide / aspect));

    void PlaceChild()
    {
        if (child == IntPtr.Zero || processExited || WindowState == FormWindowState.Minimized) return;
        var r = ScreenRect;
        var p = PointToScreen(r.Location);
        SetWindowPos(child, TopMost ? HWND_TOPMOST : IntPtr.Zero, p.X, p.Y, r.Width, r.Height,
            SWP_NOACTIVATE | (TopMost ? 0u : SWP_NOZORDER));
        if (r.Size != regionSize)
        {
            regionSize = r.Size;
            var rgn = CreateRoundRectRgn(0, 0, r.Width + 1, r.Height + 1, ScreenRadius * 2, ScreenRadius * 2);
            if (SetWindowRgn(child, rgn, true) == 0) DeleteObject(rgn);
        }
    }

    void FocusPhone()
    {
        if (child != IntPtr.Zero) SetForegroundWindow(child);
    }

    /// <summary>scrcpy resizes its own window when the phone rotates — follow it.</summary>
    void Watch()
    {
        if (child == IntPtr.Zero || processExited || WindowState == FormWindowState.Minimized) return;
        if (!GetWindowRect(child, out var r)) return;
        int cw = r.Right - r.Left, ch = r.Bottom - r.Top;
        var p = PointToScreen(ScreenRect.Location);
        if (cw <= 0 || ch <= 0) return;
        if (cw == screenSize.Width && ch == screenSize.Height && r.Left == p.X && r.Top == p.Y) return;

        double a = (double)cw / ch;
        if ((a > 1) != (aspect > 1))
        {
            int keep = LongSide;
            aspect = a;
            var wa = Screen.FromControl(this).WorkingArea;
            var center = new Point(Left + PhoneW / 2, Top + PhoneH / 2);
            SetScreenSize(SizeForLong(Math.Clamp(keep, MinLong, MaxLong(wa))));
            Location = new Point(
                Math.Clamp(center.X - PhoneW / 2, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
                Math.Clamp(center.Y - PhoneH / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        }
        PlaceChild();
    }

    void Zoom(double factor)
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int target = Math.Clamp((int)Math.Round(LongSide * factor), MinLong, MaxLong(wa));
        if (target == LongSide) return;
        var center = new Point(Left + PhoneW / 2, Top + PhoneH / 2);
        SetScreenSize(SizeForLong(target));
        Location = new Point(
            Math.Clamp(center.X - PhoneW / 2, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
            Math.Clamp(center.Y - PhoneH / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        SaveWindow?.Invoke(LongSide, Location);
    }

    // ================= hover pill =================

    void CheckHover()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        var pos = Cursor.Position;
        var phone = new Rectangle(Left, Top, PhoneW, PhoneH);
        var pill = bar.Visible ? new Rectangle(PointToScreen(bar.Location), bar.Size) : Rectangle.Empty;
        var bridge = bar.Visible ? new Rectangle(Left + PhoneW, pill.Top, Gap + 2, pill.Height) : Rectangle.Empty;
        bool inside = phone.Contains(pos) || pill.Contains(pos) || bridge.Contains(pos);
        if (inside) lastInside = DateTime.Now;
        bool show = inside || (DateTime.Now - lastInside).TotalMilliseconds < 700;
        if (show != bar.Visible)
        {
            bar.Visible = show;
            ApplyLayout();
        }
    }

    // ================= layout & shape =================

    void SetScreenSize(Size s)
    {
        layingOut = true;
        screenSize = new Size(Math.Max(s.Width, 100), Math.Max(s.Height, 100));
        LayoutBar();
        ClientSize = new Size(PhoneW + Gap + BarW, Math.Max(PhoneH, bar.Height + 16));
        layingOut = false;
        ApplyLayout();
    }

    /// <summary>Sizes the pill's buttons to fit next to the phone, even when it's small.</summary>
    void LayoutBar()
    {
        int seps = barItems.Count(c => (c.Tag as string) == "sep");
        int btns = barItems.Count - seps;
        int btnH = Math.Clamp((PhoneH - 40 - seps * 9) / Math.Max(1, btns), 20, 30);
        float font = btnH < 25 ? 8f : 9.5f;
        int y = 8;
        foreach (var c in barItems)
        {
            if ((c.Tag as string) == "sep")
            {
                c.Bounds = new Rectangle(10, y + 4, BarW - 20, 1);
                y += 9;
            }
            else
            {
                c.Bounds = new Rectangle(4, y, BarW - 8, btnH - 2);
                if (Math.Abs(c.Font.Size - font) > 0.1f) c.Font = new Font("Segoe MDL2 Assets", font);
                y += btnH;
            }
        }
        bar.Height = y + 6;
    }

    void ApplyLayout()
    {
        bar.Bounds = new Rectangle(PhoneW + Gap, Math.Clamp(PhoneH / 10, 0, Math.Max(0, ClientSize.Height - bar.Height)), BarW, bar.Height);
        using (var bp = RoundRect(new Rectangle(0, 0, bar.Width, bar.Height), BarW / 2))
            bar.Region = new Region(bp);

        var region = new Region(RoundRect(new Rectangle(0, 0, PhoneW, PhoneH), OuterRadius));
        foreach (var nub in SideButtons()) region.Union(nub);
        if (bar.Visible)
            using (var pill = RoundRect(bar.Bounds, BarW / 2))
                region.Union(pill);
        Region = region;
        PlaceChild();
        Invalidate();
    }

    IEnumerable<Rectangle> SideButtons()
    {
        int x = PhoneW, h = PhoneH;
        yield return new Rectangle(x - 1, (int)(h * 0.20), 3, (int)(h * 0.07));
        yield return new Rectangle(x - 1, (int)(h * 0.30), 3, (int)(h * 0.12));
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
        using (var black = new SolidBrush(Color.Black))
        using (var sp = RoundRect(ScreenRect, ScreenRadius))
            g.FillPath(black, sp);
        using (var nubBrush = new SolidBrush(buttonColor))
            foreach (var nub in SideButtons()) g.FillRectangle(nubBrush, nub);

        int r = Math.Max(2, Bezel / 3);
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
        if (sw > 0 && sw != screenSize.Width)
        {
            screenSize = new Size(sw, (int)Math.Round(sw / aspect));
            LayoutBar();
        }
        ApplyLayout();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        PlaceChild();
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
        const int WM_NCHITTEST = 0x84, WM_SIZING = 0x214, WM_MOVING = 0x216, WM_EXITSIZEMOVE = 0x232;
        const int HTCAPTION = 2, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
            if (p.X < PhoneW && p.Y < PhoneH)
            {
                // The rounded corners resize (keeping the phone's shape); the rest of the bezel drags.
                int c = Math.Max(OuterRadius, 22);
                bool left = p.X < c, right = p.X > PhoneW - c, top = p.Y < c, bottom = p.Y > PhoneH - c;
                m.Result = (IntPtr)(top && left ? HTTOPLEFT : top && right ? HTTOPRIGHT
                    : bottom && left ? HTBOTTOMLEFT : bottom && right ? HTBOTTOMRIGHT : HTCAPTION);
            }
            return;
        }
        if (m.Msg == WM_SIZING)
        {
            int edge = m.WParam.ToInt32();
            var rc = Marshal.PtrToStructure<RECT>(m.LParam);
            var wa = Screen.FromControl(this).WorkingArea;
            int extraW = Gap + BarW + 2 * Bezel;
            int sw = rc.Right - rc.Left - extraW;
            int sh = rc.Bottom - rc.Top - 2 * Bezel;
            // Follow whichever direction the mouse moved more, keep the aspect ratio.
            var cur = screenSize;
            bool byHeight = Math.Abs(sh - cur.Height) * aspect > Math.Abs(sw - cur.Width);
            var s = byHeight ? new Size((int)Math.Round(sh * aspect), sh) : new Size(sw, (int)Math.Round(sw / aspect));
            int lng = Math.Clamp(Math.Max(s.Width, s.Height), MinLong, MaxLong(wa) + 200);
            s = SizeForLong(lng);
            int w = s.Width + extraW, h = Math.Max(s.Height + 2 * Bezel, bar.Height + 16);
            bool fromLeft = edge is 1 or 4 or 7, fromTop = edge is 3 or 4 or 5;
            if (fromLeft) rc.Left = rc.Right - w; else rc.Right = rc.Left + w;
            if (fromTop) rc.Top = rc.Bottom - h; else rc.Bottom = rc.Top + h;
            Marshal.StructureToPtr(rc, m.LParam, false);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_MOVING) PlaceChild();
        if (m.Msg == WM_EXITSIZEMOVE) SaveWindow?.Invoke(LongSide, Location);
    }

    // ================= buttons =================

    void Key(int code) => _ = Tools.AdbAsync(5000, "-s", device.Serial, "shell", "input", "keyevent", code.ToString());

    void TogglePin()
    {
        TopMost = !TopMost;
        if (child != IntPtr.Zero)
            SetWindowPos(child, TopMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        UpdatePin();
        PlaceChild();
    }

    void UpdatePin() => pinBtn.ForeColor = TopMost ? Theme.Accent : iconColor;

    // ================= closing =================

    public void ProcessExited()
    {
        processExited = true;
        watch.Stop();
        hoverTimer.Stop();
        if (!IsDisposed) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!processExited && child != IntPtr.Zero)
        {
            e.Cancel = true;
            if (!closeRequested)
            {
                closeRequested = true;
                SaveWindow?.Invoke(LongSide, Location);
                PostMessage(child, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                _ = Task.Delay(3000).ContinueWith(_ => { try { if (!proc.HasExited) proc.Kill(); } catch { } });
            }
            return;
        }
        watch.Stop();
        hoverTimer.Stop();
        base.OnFormClosing(e);
    }

    // ================= Win32 =================

    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, SW_HIDE = 0, SW_SHOW = 5;
    const long WS_EX_APPWINDOW = 0x00040000L, WS_EX_TOOLWINDOW = 0x00000080L;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
}
