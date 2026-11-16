namespace PhoneMirror;

static class Theme
{
    public static readonly Color Bg     = Color.FromArgb(18, 20, 25);
    public static readonly Color Panel  = Color.FromArgb(28, 31, 38);
    public static readonly Color Panel2 = Color.FromArgb(40, 44, 53);
    public static readonly Color Hover  = Color.FromArgb(54, 59, 70);
    public static readonly Color Accent = Color.FromArgb(59, 130, 246);
    public static readonly Color AccentHover = Color.FromArgb(96, 155, 250);
    public static readonly Color Text   = Color.FromArgb(235, 238, 243);
    public static readonly Color Muted  = Color.FromArgb(148, 155, 168);
    public static readonly Color Good   = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn   = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad    = Color.FromArgb(248, 113, 113);

    public static Button Button(string text, bool accent = false)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Accent : Panel2,
            ForeColor = accent ? Color.White : Text,
            Font = new Font("Segoe UI", accent ? 13f : 9.5f, accent ? FontStyle.Bold : FontStyle.Regular),
            AutoSize = !accent,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : Hover;
        b.FlatAppearance.MouseDownBackColor = accent ? AccentHover : Hover;
        return b;
    }

    public static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        AutoSize = true,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        Margin = new Padding(0, 14, 0, 6),
    };
}
