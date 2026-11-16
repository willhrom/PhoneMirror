using System.Security.Cryptography;
using System.Text.RegularExpressions;
using QRCoder;

namespace PhoneMirror;

/// <summary>
/// Android 11+ "Pair device with QR code": shows a QR code, waits for the phone to
/// announce itself over mDNS, then pairs and connects — no typing.
/// </summary>
class QrPairForm : Form
{
    readonly string name = "phonemirror-" + RandomNumberGenerator.GetHexString(6, true);
    readonly string pass = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 10);
    readonly Label status = new();
    readonly CancellationTokenSource cts = new();

    public string? ConnectedTo { get; private set; }

    public QrPairForm()
    {
        Text = "Add phone wirelessly";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(22);

        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(22, 18),
        };

        flow.Controls.Add(new Label { Text = "Scan with your phone", AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Margin = new Padding(0, 0, 0, 8) });
        flow.Controls.Add(new Label
        {
            Text = "1.  Put your phone on the same Wi-Fi as this laptop.\n" +
                   "2.  On the phone: Settings › Developer options › Wireless debugging › turn it ON.\n" +
                   "3.  Tap \"Pair device with QR code\" and scan this:",
            AutoSize = true,
            MaximumSize = new Size(400, 0),
            ForeColor = Theme.Muted,
            Margin = new Padding(0, 0, 0, 14),
        });

        using (var gen = new QRCodeGenerator())
        using (var data = gen.CreateQrCode($"WIFI:T:ADB;S:{name};P:{pass};;", QRCodeGenerator.ECCLevel.M))
        {
            var png = new PngByteQRCode(data).GetGraphic(10);
            var pic = new PictureBox
            {
                Image = Image.FromStream(new MemoryStream(png)),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(260, 260),
                BackColor = Color.White,
                Margin = new Padding(70, 0, 0, 14),
            };
            flow.Controls.Add(pic);
        }

        status.Text = "Waiting for your phone…";
        status.AutoSize = true;
        status.MaximumSize = new Size(400, 0);
        status.ForeColor = Theme.Warn;
        status.Margin = new Padding(0, 0, 0, 14);
        flow.Controls.Add(status);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        var code = Theme.Button("Use a pairing code instead");
        code.DialogResult = DialogResult.Retry;
        var cancel = Theme.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(code);
        buttons.Controls.Add(cancel);
        flow.Controls.Add(buttons);

        Controls.Add(flow);
        CancelButton = cancel;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { await PairLoop(cts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) SetStatus("Something went wrong: " + ex.Message, Theme.Bad); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        cts.Cancel();
        base.OnFormClosing(e);
    }

    void SetStatus(string text, Color color)
    {
        status.Text = text;
        status.ForeColor = color;
    }

    static readonly Regex Addr = new(@"(\d+\.\d+\.\d+\.\d+):(\d+)");

    async Task PairLoop(CancellationToken ct)
    {
        var deadline = DateTime.Now.AddMinutes(3);
        string? pairAddr = null;
        while (pairAddr == null)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.Now > deadline)
            {
                SetStatus("Timed out. Make sure both are on the same Wi-Fi, then try again — or use a pairing code.", Theme.Bad);
                return;
            }
            var (_, o) = await Tools.AdbAsync(5000, "mdns", "services");
            foreach (var line in o.Split('\n'))
                if (line.Contains(name) && line.Contains("_adb-tls-pairing"))
                {
                    var m = Addr.Match(line);
                    if (m.Success) pairAddr = m.Value;
                }
            if (pairAddr == null) await Task.Delay(1000, ct);
        }

        SetStatus("Phone found — pairing…", Theme.Warn);
        var (_, r) = await Tools.AdbAsync(20000, "pair", pairAddr, pass);
        ct.ThrowIfCancellationRequested();
        if (!r.Contains("Successfully", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Pairing failed: " + r.Split('\n')[0].Trim(), Theme.Bad);
            return;
        }

        SetStatus("Paired! Connecting…", Theme.Good);
        var ip = pairAddr.Split(':')[0];
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();
            var devs = await Tools.DevicesAsync();
            var d = devs.FirstOrDefault(x => x.Ready && x.Wireless && (x.Serial.StartsWith(ip + ":") || x.Serial.Contains("_adb-tls-connect")));
            if (d != null)
            {
                ConnectedTo = d.Serial;
                DialogResult = DialogResult.OK;
                return;
            }
            var (_, s) = await Tools.AdbAsync(5000, "mdns", "services");
            foreach (var line in s.Split('\n'))
                if (line.Contains("_adb-tls-connect") && line.Contains(ip + ":"))
                {
                    var m = Addr.Match(line);
                    if (m.Success) await Tools.AdbAsync(8000, "connect", m.Value);
                }
            await Task.Delay(1000, ct);
        }
        SetStatus("Paired, but couldn't connect. Use \"Connect by IP\" with the address shown on the Wireless debugging screen.", Theme.Bad);
    }
}
