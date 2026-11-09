using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui.Controls;

internal enum LabelTone { Primary, Secondary, Tertiary, Disabled, Danger, Success, Warning, Accent }

internal sealed class ThemedLabel : Label
{
    public LabelTone Tone { get; set; } = LabelTone.Primary;

    public ThemedLabel()
    {
        AutoSize = true;
        Font = Typography.Body;
        BackColor = Color.Transparent;
        Theme.Bind(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var color = Tone switch
        {
            LabelTone.Secondary => Theme.Current.TextSecondary,
            LabelTone.Tertiary => Theme.Current.TextTertiary,
            LabelTone.Disabled => Theme.Current.TextDisabled,
            LabelTone.Danger => Theme.Current.DangerText,
            LabelTone.Success => Theme.Current.SuccessText,
            LabelTone.Warning => Theme.Current.WarningText,
            LabelTone.Accent => Theme.Current.Accent,
            _ => Theme.Current.TextPrimary
        };
        if (ForeColor != color) ForeColor = color;
        base.OnPaint(e);
    }
}

internal class RoundedPanel : Panel
{
    public int Radius { get; set; } = 10;
    public Color? FillColor { get; set; }
    public Color? BorderColor { get; set; }
    public int BorderWidth { get; set; } = 1;

    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Theme.Bind(this);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // The rounded path is painted in OnPaint so the corners stay transparent.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var fill = FillColor ?? Theme.Current.Surface;
        Draw.FillRounded(e.Graphics, bounds, Radius, fill);
        if (BorderWidth > 0)
            Draw.StrokeRounded(e.Graphics, bounds, Radius, BorderColor ?? Theme.Current.Border, BorderWidth);
    }
}

internal sealed class StatusDot : Control
{
    private Color dotColor = Color.Gray;

    public Color DotColor
    {
        get => dotColor;
        set { if (dotColor != value) { dotColor = value; Invalidate(); } }
    }

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(14, 14);
        Theme.Bind(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var c = DotColor;
        var halo = Draw.Scale(this, 14);
        var dot = Draw.Scale(this, 10);
        var hx = (Width - halo) / 2;
        var hy = (Height - halo) / 2;
        using (var brush = new SolidBrush(Color.FromArgb(40, c.R, c.G, c.B)))
            e.Graphics.FillEllipse(brush, hx, hy, halo, halo);
        var dx = (Width - dot) / 2;
        var dy = (Height - dot) / 2;
        using var dotBrush = new SolidBrush(c);
        e.Graphics.FillEllipse(dotBrush, dx, dy, dot, dot);
    }
}

internal sealed class Spinner : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private float angle;

    public Spinner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(16, 16);
        timer.Tick += (_, _) => { angle = (angle + 14) % 360; Invalidate(); };
        Theme.Bind(this);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && SystemInformation.UIEffectsEnabled) timer.Start();
        else timer.Stop();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var thickness = Math.Max(2f, Draw.ScaleF(this, 2f));
        var pad = (int)Math.Ceiling(thickness / 2f) + 1;
        var rect = new Rectangle(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
        using var track = new Pen(Draw.Blend(Theme.Current.Warning, Theme.Current.Surface, 0.72), thickness);
        e.Graphics.DrawArc(track, rect, 0, 360);
        using var pen = new Pen(Theme.Current.Warning, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawArc(pen, rect, angle, 110);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
