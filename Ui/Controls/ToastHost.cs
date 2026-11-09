using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui.Controls;

internal enum ToastKind { Info, Success, Warning, Error }

internal sealed class ToastHost : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2600 };
    private string message = "";
    private ToastKind kind;

    public ToastHost()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        Visible = false;
        Width = 300;
        Height = 42;
        BackColor = Color.Transparent;
        timer.Tick += (_, _) => { Visible = false; timer.Stop(); };
        Theme.Bind(this);
    }

    public void Show(string text, ToastKind toastKind)
    {
        message = text;
        kind = toastKind;
        var p = Theme.Current;
        var measured = TextRenderer.MeasureText(text, Typography.Body, new Size(Draw.Scale(this, 280), 1000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        Width = Math.Min(Draw.Scale(this, 360), Math.Max(Draw.Scale(this, 220), measured.Width + Draw.Scale(this, 54)));
        Height = Math.Max(Draw.Scale(this, 42), measured.Height + Draw.Scale(this, 20));
        Visible = true;
        BringToFront();
        timer.Stop();
        timer.Start();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Draw.Scale(this, 10);
        Draw.FillRounded(e.Graphics, bounds, radius, p.Surface);
        Draw.StrokeRounded(e.Graphics, bounds, radius, p.Border);
        var (glyph, color) = kind switch
        {
            ToastKind.Success => (Icons.Check, p.SuccessText),
            ToastKind.Warning => (Icons.Warning, p.WarningText),
            ToastKind.Error => (Icons.Error, p.DangerText),
            _ => (Icons.Info, p.Accent)
        };
        var icon = Draw.Scale(this, 16);
        Icons.Draw(e.Graphics, glyph, new Rectangle(Draw.Scale(this, 14), Draw.Scale(this, 13), icon, icon), color, icon);
        var textX = Draw.Scale(this, 40);
        TextRenderer.DrawText(e.Graphics, message, Typography.Body,
            new Rectangle(textX, Draw.Scale(this, 10), Width - textX - Draw.Scale(this, 14), Height - Draw.Scale(this, 20)),
            p.TextPrimary, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.Left);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
