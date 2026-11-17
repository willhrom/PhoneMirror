using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PhoneMirror;

/// <summary>
/// A borderless, phone-shaped window (bezel, camera, side buttons). The scrcpy video window
/// stays its own window but is owned by this frame and kept exactly over the frame's screen
/// area. Controls live in a separate floating dock that fades in below the phone.
/// Resize from any corner or with the dock's +/− buttons; size and position are remembered.
/// </summary>
class PhoneFrameForm : Form
{
    const int MinLong = 300;

    readonly IMirrorSource src;
    readonly Color bodyColor, edgeColor, buttonColor;
    readonly string dockMode;
    readonly int savedLong;
    readonly Point? savedPos;
    readonly DockForm dock;
    readonly System.Windows.Forms.Timer watch = new() { Interval = 150 };
    readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 80 };

    IntPtr child;
    double aspect = 9.0 / 19.5;
    Size screenSize = new(360, 780);
    bool processExited, closeRequested, layingOut;
    DateTime lastInside = DateTime.MinValue;

    public Action<string>? Log;
    public Action<int, Point>? SaveWindow;

    int BezelFor(int screenW) => Math.Clamp(screenW / 30, 6, 16);
    int Bezel => BezelFor(screenSize.Width);
    static int OuterRadiusFor(Size screen) => Math.Clamp(Math.Min(screen.Width, screen.Height) / 7, 16, 60);
    int ScreenRadiusFor(Size screen) => Math.Max(6, OuterRadiusFor(screen) - BezelFor(screen.Width));
    int OuterRadius => OuterRadiusFor(screenSize);
    Size regionSize;
    int PhoneW => screenSize.Width + 2 * Bezel;
    int PhoneH => screenSize.Height + 2 * Bezel;
    Rectangle ScreenRect => new(Bezel, Bezel, screenSize.Width, screenSize.Height);
    int LongSide => Math.Max(screenSize.Width, screenSize.Height);

    public PhoneFrameForm(IMirrorSource src, string style, bool topMost, string dockMode, int savedLong, Point? savedPos)
    {
        this.src = src;
        this.dockMode = dockMode;
        this.savedLong = savedLong;
        this.savedPos = savedPos;
        (bodyColor, edgeColor, buttonColor) = style switch
        {
            "Silver" => (Color.FromArgb(214, 216, 221), Color.FromArgb(160, 163, 170), Color.FromArgb(186, 189, 195)),
            "Blue" => (Color.FromArgb(38, 58, 100), Color.FromArgb(84, 112, 168), Color.FromArgb(58, 82, 130)),
            _ => (Color.FromArgb(22, 22, 25), Color.FromArgb(70, 70, 78), Color.FromArgb(46, 46, 52)),
        };

        Text = $"{src.Name} — Phone Mirror";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = bodyColor;
        DoubleBuffered = true;
        TopMost = topMost;
        ShowInTaskbar = true;

        dock = new DockForm(style, new (string, string, Action?)[]
        {
            ("", "Back", () => Key("back")),
            ("", "Home", () => Key("home")),
            ("", "Recent apps", () => Key("recents")),
            ("", "", null),
            ("", "Screenshot", () => { _ = src.Screenshot(); }),
            ("", "Volume down", () => Key("voldown")),
            ("", "Volume up", () => Key("volup")),
            ("", "Power / lock", () => Key("power")),
            ("", "", null),
            ("", "Smaller", () => Zoom(1 / 1.15)),
            ("", "Bigger", () => Zoom(1.15)),
            ("", "Keep on top", TogglePin),
            ("", "", null),
            ("", "Minimize", () => WindowState = FormWindowState.Minimized),
            ("", "Close", () => Close()),
        });
        dock.SetPinned(topMost);

        watch.Tick += (_, _) => Watch();
        hoverTimer.Tick += (_, _) => CheckHover();
    }

    // ================= attaching =================

    public async Task<bool> AttachAsync(Screen display)
    {
        for (int i = 0; i < 120 && child == IntPtr.Zero; i++)
        {
            if (src.HasExited) return false;
            child = src.FindWindow();
            if (child == IntPtr.Zero) await Task.Delay(150);
        }
        if (child == IntPtr.Zero) { Log?.Invoke("Phone frame: couldn't find the mirror window."); return false; }

        GetClientRect(child, out var rc);
        int cw = Math.Max(1, rc.Right - rc.Left), ch = Math.Max(1, rc.Bottom - rc.Top);
        aspect = (double)cw / ch;

        if (savedPos is Point sp && Screen.AllScreens.Any(s => s.WorkingArea.Contains(sp)))
            display = Screen.FromPoint(sp);
        var wa = display.WorkingArea;
        int wanted = savedLong > 0 ? savedLong : (aspect < 1 ? Math.Max(ch, 640) : Math.Max(cw, 760));
        SetScreenSize(SizeForLong(Math.Clamp(wanted, MinLong, MaxLong(wa))));
        if (savedPos is Point p && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(p, Size))))
            Location = p;
        else
            Location = new Point(wa.Right - Width - 80, wa.Top + (wa.Height - Height) / 2 - 20);

        Show();
        dock.Show(this);
        dock.Opacity = 0;
        if (dockMode == "always") dock.FadeTo(1);

        ShowWindow(child, SW_HIDE);
        var ex = (long)GetWindowLongPtr(child, GWL_EXSTYLE);
        ex = (ex & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(child, GWL_EXSTYLE, (IntPtr)ex);
        SetWindowLongPtr(child, GWLP_HWNDPARENT, Handle);
        PlaceAll(Bounds);
        ShowWindow(child, SW_SHOW);
        SetForegroundWindow(child);
        watch.Start();
        if (dockMode == "hover") hoverTimer.Start();
        return true;
    }

    int MaxLong(Rectangle wa) => aspect < 1 ? (int)(wa.Height * 0.92) - 16 : (int)(wa.Width * 0.85);

    Size SizeForLong(int longSide) => aspect < 1
        ? new Size((int)Math.Round(longSide * aspect), longSide)
        : new Size(longSide, (int)Math.Round(longSide / aspect));

    /// <summary>Places the video window and the dock for a frame at <paramref name="frame"/> (screen coords).</summary>
    void PlaceAll(Rectangle frame) => PlaceAll(frame, screenSize);

    void PlaceAll(Rectangle frame, Size screen)
    {
        if (processExited || WindowState == FormWindowState.Minimized) return;
        int b = BezelFor(screen.Width);
        if (child != IntPtr.Zero)
        {
            SetWindowPos(child, TopMost ? HWND_TOPMOST : IntPtr.Zero, frame.X + b, frame.Y + b, screen.Width, screen.Height,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER | (TopMost ? 0u : SWP_NOZORDER));
            // Round the picture's corners to sit inside the bezel (only when the size changes).
            if (screen != regionSize)
            {
                regionSize = screen;
                int d = ScreenRadiusFor(screen) * 2;
                var rgn = CreateRoundRectRgn(0, 0, screen.Width + 1, screen.Height + 1, d, d);
                if (SetWindowRgn(child, rgn, true) == 0) DeleteObject(rgn);
            }
        }
        PlaceDock(frame);
    }

    void PlaceDock(Rectangle frame)
    {
        if (!dock.Visible) return;
        var wa = Screen.FromRectangle(frame).WorkingArea;
        int x = frame.X + frame.Width / 2 - dock.Width / 2;
        int y = frame.Bottom + 10;
        if (y + dock.Height > wa.Bottom) y = frame.Top - dock.Height - 10;      // no room below: go above
        if (y < wa.Top) y = frame.Bottom - dock.Height - 18;                     // no room either: tuck inside
        x = Math.Clamp(x, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - dock.Width - 4));
        dock.MoveTo(new Point(x, y));
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
        if (cw <= 0 || ch <= 0) return;
        if (cw == screenSize.Width && ch == screenSize.Height && r.Left == Left + Bezel && r.Top == Top + Bezel) return;

        double a = (double)cw / ch;
        if ((a > 1) != (aspect > 1))
        {
            int keep = LongSide;
            aspect = a;
            var wa = Screen.FromControl(this).WorkingArea;
            var center = new Point(Left + Width / 2, Top + Height / 2);
            SetScreenSize(SizeForLong(Math.Clamp(keep, MinLong, MaxLong(wa))));
            Location = ClampTo(wa, new Point(center.X - Width / 2, center.Y - Height / 2));
        }
        PlaceAll(Bounds);
    }

    Point ClampTo(Rectangle wa, Point p) => new(
        Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
        Math.Clamp(p.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));

    void Zoom(double factor)
    {
        var wa = Screen.FromControl(this).WorkingArea;
        int target = Math.Clamp((int)Math.Round(LongSide * factor), MinLong, MaxLong(wa));
        if (target == LongSide) return;
        var center = new Point(Left + Width / 2, Top + Height / 2);
        SetScreenSize(SizeForLong(target));
        Location = ClampTo(wa, new Point(center.X - Width / 2, center.Y - Height / 2));
        PlaceAll(Bounds);
        SaveWindow?.Invoke(LongSide, Location);
    }

    // ================= hover dock =================

    void CheckHover()
    {
        if (!Visible || WindowState == FormWindowState.Minimized) return;
        var pos = Cursor.Position;
        var phone = Bounds;
        phone.Inflate(6, 6);
        bool inside = phone.Contains(pos) || (dock.Opacity > 0.05 && (dock.Bounds.Contains(pos) || InGap(pos)));
        if (inside) lastInside = DateTime.Now;
        bool show = inside || (DateTime.Now - lastInside).TotalMilliseconds < 600;
        dock.FadeTo(show ? 1 : 0);
    }

    bool InGap(Point pos)
    {
        // the strip between the phone and the dock, as wide as the dock
        var d = dock.Bounds;
        var gap = d.Top >= Bottom ? new Rectangle(d.Left, Bottom, d.Width, d.Top - Bottom)
                : d.Bottom <= Top ? new Rectangle(d.Left, d.Bottom, d.Width, Top - d.Bottom)
                : Rectangle.Empty;
        return gap.Contains(pos);
    }

    // ================= layout & shape =================

    void SetScreenSize(Size s)
    {
        layingOut = true;
        screenSize = new Size(Math.Max(s.Width, 100), Math.Max(s.Height, 100));
        ClientSize = new Size(PhoneW, PhoneH);
        layingOut = false;
        ApplyShape();
    }

    void ApplyShape() => Render(null, screenSize);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED: per-pixel alpha, so the phone's edges are smooth
            return cp;
        }
    }

    /// <summary>
    /// Draws the phone body into an anti-aliased, transparent bitmap and hands it to Windows
    /// as the window's look (smooth rounded edges; the corners outside are see-through and click-through).
    /// </summary>
    void Render(Point? at, Size screen)
    {
        var hwnd = Handle; // make sure the window exists before giving it its look
        int b = BezelFor(screen.Width), R = OuterRadiusFor(screen), r = ScreenRadiusFor(screen);
        int w = screen.Width + 2 * b, h = screen.Height + 2 * b;
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            using (var bodyBrush = new SolidBrush(bodyColor))
            using (var path = RoundRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), R))
                g.FillPath(bodyBrush, path);
            using (var pen = new Pen(edgeColor, 1.6f))
            using (var path = RoundRect(new RectangleF(1.3f, 1.3f, w - 2.6f, h - 2.6f), R - 1))
                g.DrawPath(pen, path);
            using (var black = new SolidBrush(Color.Black))
            using (var hole = RoundRect(new RectangleF(b - 0.5f, b - 0.5f, screen.Width + 1f, screen.Height + 1f), r + 0.5f))
                g.FillPath(black, hole);
            using (var nub = new Pen(buttonColor, 2f))
            {
                float x = w - 1.5f;
                g.DrawLine(nub, x, h * 0.20f, x, h * 0.27f);
                g.DrawLine(nub, x, h * 0.31f, x, h * 0.43f);
            }
            float cr = Math.Max(2f, b / 3f);
            using (var camBrush = new SolidBrush(Color.FromArgb(8, 8, 10)))
                g.FillEllipse(camBrush, w / 2f - cr, b / 2f - cr, cr * 2, cr * 2);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (at is Point p)
            {
                var dst = new POINT { x = p.X, y = p.Y };
                UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            else UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (layingOut || WindowState == FormWindowState.Minimized) return;
        int sw = ClientSize.Width - 2 * Bezel;
        if (sw > 0 && sw != screenSize.Width)
            screenSize = new Size(sw, (int)Math.Round(sw / aspect));
        ApplyShape();
        PlaceAll(Bounds);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        PlaceAll(Bounds);
    }

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Max(2f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
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
        switch (m.Msg)
        {
            case WM_NCHITTEST:
            {
                base.WndProc(ref m);
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                int c = Math.Max(OuterRadius, 22);
                bool left = p.X < c, right = p.X > PhoneW - c, top = p.Y < c, bottom = p.Y > PhoneH - c;
                m.Result = (IntPtr)(top && left ? HTTOPLEFT : top && right ? HTTOPRIGHT
                    : bottom && left ? HTBOTTOMLEFT : bottom && right ? HTBOTTOMRIGHT : HTCAPTION);
                return;
            }
            case WM_MOVING:
            {
                // Move the picture and dock to where the frame is *about* to be, so nothing trails behind.
                var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                PlaceAll(new Rectangle(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top));
                base.WndProc(ref m);
                return;
            }
            case WM_SIZING:
            {
                int edge = m.WParam.ToInt32();
                var rc = Marshal.PtrToStructure<RECT>(m.LParam);
                var wa = Screen.FromControl(this).WorkingArea;
                int b = Bezel;
                int sw = rc.Right - rc.Left - 2 * b, sh = rc.Bottom - rc.Top - 2 * b;
                bool byHeight = Math.Abs(sh - screenSize.Height) * aspect > Math.Abs(sw - screenSize.Width);
                var s = byHeight ? new Size((int)Math.Round(sh * aspect), sh) : new Size(sw, (int)Math.Round(sw / aspect));
                s = SizeForLong(Math.Clamp(Math.Max(s.Width, s.Height), MinLong, MaxLong(wa) + 200));
                int nb = BezelFor(s.Width);
                int w = s.Width + 2 * nb, h = s.Height + 2 * nb;
                bool fromLeft = edge is 1 or 4 or 7, fromTop = edge is 3 or 4 or 5;
                if (fromLeft) rc.Left = rc.Right - w; else rc.Right = rc.Left + w;
                if (fromTop) rc.Top = rc.Bottom - h; else rc.Bottom = rc.Top + h;
                Marshal.StructureToPtr(rc, m.LParam, false);
                Render(new Point(rc.Left, rc.Top), s);
                PlaceAll(new Rectangle(rc.Left, rc.Top, w, h), s);
                m.Result = (IntPtr)1;
                return;
            }
            case WM_EXITSIZEMOVE:
                base.WndProc(ref m);
                PlaceAll(Bounds);
                SaveWindow?.Invoke(LongSide, Location);
                return;
        }
        base.WndProc(ref m);
    }

    // ================= buttons =================

    void Key(string key)
    {
        src.Key(key);
        FocusPhone();
    }

    void TogglePin()
    {
        TopMost = !TopMost;
        dock.TopMost = TopMost;
        if (child != IntPtr.Zero)
            SetWindowPos(child, TopMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        dock.SetPinned(TopMost);
        PlaceAll(Bounds);
    }

    // ================= closing =================

    /// <summary>Releases the video window so it no longer depends on this frame.</summary>
    public void Detach(bool closeMirror = false)
    {
        if (child == IntPtr.Zero) return;
        watch.Stop();
        hoverTimer.Stop();
        try { SetWindowLongPtr(child, GWLP_HWNDPARENT, IntPtr.Zero); } catch { }
        if (closeMirror) PostMessage(child, 0x0010 /* WM_CLOSE: scrcpy quits cleanly, saving recordings */, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Closes the frame without waiting for the mirror (used when the app quits).</summary>
    public void ForceClose()
    {
        Detach();
        processExited = true;
        if (!IsDisposed) Close();
    }

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
                dock.Hide();
                SaveWindow?.Invoke(LongSide, Location);
                PostMessage(child, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                _ = Task.Delay(3000).ContinueWith(_ => { try { if (!src.HasExited) src.Kill(); } catch { } });
            }
            return;
        }
        watch.Stop();
        hoverTimer.Stop();
        try { dock.Close(); } catch { }
        base.OnFormClosing(e);
    }

    // ================= Win32 =================

    const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, SW_HIDE = 0, SW_SHOW = 5;
    const long WS_EX_APPWINDOW = 0x00040000L, WS_EX_TOOLWINDOW = 0x00000080L;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
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

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
}

/// <summary>What the phone frame shows: a USB debugging (scrcpy) mirror or a phone-app mirror.</summary>
interface IMirrorSource
{
    string Name { get; }
    bool HasExited { get; }
    /// <summary>The video window, or zero until it's ready.</summary>
    IntPtr FindWindow();
    /// <summary>back, home, recents, power, volup, voldown</summary>
    void Key(string key);
    Task Screenshot();
    void Kill();
}

/// <summary>Mirror through USB debugging: a scrcpy process.</summary>
class ScrcpySource : IMirrorSource
{
    readonly Process proc;
    readonly Device device;
    readonly Func<Device, Task> screenshot;

    public ScrcpySource(Process proc, Device device, string name, Func<Device, Task> screenshot)
    {
        this.proc = proc;
        this.device = device;
        this.screenshot = screenshot;
        Name = name;
    }

    public string Name { get; }
    public bool HasExited { get { try { return proc.HasExited; } catch { return true; } } }

    public IntPtr FindWindow()
    {
        try { proc.Refresh(); return proc.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    public void Key(string key)
    {
        int code = key switch { "back" => 4, "home" => 3, "recents" => 187, "power" => 26, "volup" => 24, "voldown" => 25, _ => 0 };
        if (code != 0) _ = Tools.AdbAsync(5000, "-s", device.Serial, "shell", "input", "keyevent", code.ToString());
    }

    public Task Screenshot() => screenshot(device);
    public void Kill() { try { proc.Kill(); } catch { } }
}

/// <summary>
/// The floating control dock below the phone: a rounded, slightly see-through pill that
/// fades in and out and never takes keyboard focus away from the phone.
/// </summary>
class DockForm : Form
{
    const int BtnW = 34, H = 44;
    readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };
    readonly Button pin;
    readonly Color iconColor;
    double target;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    public DockForm(string style, (string Glyph, string Tip, Action? Act)[] items)
    {
        bool light = style == "Silver";
        BackColor = style switch
        {
            "Silver" => Color.FromArgb(236, 237, 240),
            "Blue" => Color.FromArgb(28, 44, 78),
            _ => Color.FromArgb(24, 25, 30),
        };
        iconColor = light ? Color.FromArgb(55, 58, 66) : Color.FromArgb(214, 218, 228);
        var hover = light ? Color.FromArgb(214, 217, 223) : ControlPaint.Light(BackColor, 0.45f);
        var sepColor = light ? Color.FromArgb(205, 208, 214) : ControlPaint.Light(BackColor, 0.3f);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        Opacity = 0;

        var tips = new ToolTip();
        int x = 8;
        Button? pinBtn = null;
        foreach (var (glyph, tip, act) in items)
        {
            if (act == null)
            {
                Controls.Add(new Panel { BackColor = sepColor, Bounds = new Rectangle(x + 4, 12, 1, H - 24) });
                x += 9;
                continue;
            }
            var b = new Button
            {
                Text = glyph,
                Font = new Font("Segoe MDL2 Assets", 10f),
                FlatStyle = FlatStyle.Flat,
                ForeColor = iconColor,
                BackColor = BackColor,
                Bounds = new Rectangle(x, 6, BtnW, H - 12),
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = hover;
            b.FlatAppearance.MouseDownBackColor = hover;
            b.Click += (_, _) => act();
            using (var path = Round(new Rectangle(0, 0, b.Width, b.Height), 9)) b.Region = new Region(path);
            tips.SetToolTip(b, tip);
            Controls.Add(b);
            if (tip == "Keep on top") pinBtn = b;
            x += BtnW + 2;
        }
        pin = pinBtn!;
        ClientSize = new Size(x + 6, H);
        using (var p = Round(new Rectangle(0, 0, Width, Height), H / 2)) Region = new Region(p);

        fade.Tick += (_, _) =>
        {
            double step = 0.12;
            double o = Opacity;
            o = target > o ? Math.Min(target, o + step) : Math.Max(target, o - step * 0.8);
            Opacity = o;
            if (Math.Abs(o - target) < 0.001) fade.Stop();
        };
    }

    public void FadeTo(double value)
    {
        value = value > 0 ? 0.96 : 0;
        if (Math.Abs(value - target) < 0.001 && (fade.Enabled || Math.Abs(Opacity - value) < 0.001)) return;
        target = value;
        fade.Start();
    }

    public void MoveTo(Point p)
    {
        if (Location != p) SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, 0, 0, 0x1 | 0x4 | 0x10 /* NOSIZE|NOZORDER|NOACTIVATE */);
    }

    public void SetPinned(bool on) => pin.ForeColor = on ? Theme.Accent : iconColor;

    static GraphicsPath Round(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
}
