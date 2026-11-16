namespace PhoneMirror;

/// <summary>Small dark-themed input dialog with one or more text fields.</summary>
static class Prompt
{
    public static string[]? Show(IWin32Window owner, string title, string intro, params (string Label, string Value)[] fields)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Bg,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 9.5f),
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18),
        };

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(18, 18),
        };

        if (!string.IsNullOrWhiteSpace(intro))
            flow.Controls.Add(new Label
            {
                Text = intro,
                AutoSize = true,
                MaximumSize = new Size(440, 0),
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 0, 0, 12),
            });

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            flow.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
            var tb = new TextBox
            {
                Text = value,
                Width = 440,
                BackColor = Theme.Panel2,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 0, 0, 12),
            };
            boxes.Add(tb);
            flow.Controls.Add(tb);
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        var ok = Theme.Button("OK");
        ok.BackColor = Theme.Accent;
        ok.ForeColor = Color.White;
        ok.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
        ok.DialogResult = DialogResult.OK;
        var cancel = Theme.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        flow.Controls.Add(buttons);

        form.Controls.Add(flow);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { if (boxes.Count > 0) { boxes[0].Focus(); boxes[0].SelectAll(); } };

        return form.ShowDialog(owner) == DialogResult.OK ? boxes.Select(b => b.Text.Trim()).ToArray() : null;
    }
}
