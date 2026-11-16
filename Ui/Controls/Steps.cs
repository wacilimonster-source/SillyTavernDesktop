using System.Drawing.Drawing2D;
using SillyTavernTray.Ui;

namespace SillyTavernTray.Ui.Controls;

/// <summary>
/// Horizontal chip list for maintenance steps (v2 §3.1 StepProgress).
/// Each chip is individually focusable so screen readers can walk the steps.
/// </summary>
internal sealed class StepProgress : FlowLayoutPanel
{
    public StepProgress()
    {
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = true;
        AutoSize = false;
        Height = 26;
        Margin = Padding.Empty;
        BackColor = Color.Transparent;
        Theme.Bind(this);
    }

    public void SetSteps(IReadOnlyList<StepState> steps)
    {
        SuspendLayout();
        foreach (Control existing in Controls) existing.Dispose();
        Controls.Clear();
        for (var i = 0; i < steps.Count; i++) Controls.Add(new StepChip(i + 1, steps[i].Label, steps[i].Status));
        ResumeLayout(true);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Transparent: the status card surface shows through.
    }
}

internal sealed class StepChip : Control
{
    private static readonly string[] StatusWords = { "待执行", "进行中", "已完成", "失败" };
    private readonly int number;
    private readonly string label;
    private StepStatus status;

    public StepChip(int number, string label, StepStatus status)
    {
        this.number = number;
        this.label = label;
        this.status = status;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Typography.Caption;
        Cursor = Cursors.Default;
        TabStop = true;
        AccessibleRole = AccessibleRole.ListItem;
        UpdateAccessibleName();
        AutoSize = true;
        Margin = new Padding(0, 0, 6, 0);
        Theme.Bind(this);
    }

    private void UpdateAccessibleName() =>
        AccessibleName = $"第 {number} 步，{label}，{StatusWords[(int)status]}";

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textWidth = TextRenderer.MeasureText(label, Font, new Size(4000, 100), TextFormatFlags.NoPrefix).Width;
        var dot = Draw.Scale(this, 13);
        return new Size(dot + Draw.Scale(this, 5) + textWidth + Draw.Scale(this, 18), Draw.Scale(this, 24));
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Height / 2;

        Color border = p.Border, fill = p.Surface, textColor = p.TextSecondary;
        if (status == StepStatus.Done) { border = Draw.Blend(p.Success, p.Border, 0.65f); textColor = p.TextPrimary; }
        else if (status == StepStatus.Active) { border = p.Accent; textColor = p.TextPrimary; }
        else if (status == StepStatus.Failed) { border = p.Danger; textColor = p.DangerText; }
        Draw.FillRounded(e.Graphics, bounds, radius, fill);
        Draw.StrokeRounded(e.Graphics, bounds, radius, border);

        var dot = Draw.Scale(this, 13);
        var dotRect = new Rectangle(Draw.Scale(this, 8), (Height - dot) / 2, dot, dot);
        var textRect = new Rectangle(dotRect.Right + Draw.Scale(this, 5), 0,
            Math.Max(0, Width - dotRect.Right - Draw.Scale(this, 13)), Height);
        switch (status)
        {
            case StepStatus.Done:
                using (var brush = new SolidBrush(p.Success)) e.Graphics.FillEllipse(brush, dotRect);
                Icons.Draw(e.Graphics, Icons.Check, dotRect, Color.White, dot - 2);
                break;
            case StepStatus.Failed:
                using (var brush = new SolidBrush(p.Danger)) e.Graphics.FillEllipse(brush, dotRect);
                Icons.Draw(e.Graphics, Icons.Close, dotRect, Color.White, dot - 2);
                break;
            case StepStatus.Active:
                using (var pen = new Pen(p.Accent, Draw.ScaleF(this, 1.6f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    // Three-quarter arc reads as "working" without an animation timer.
                    e.Graphics.DrawArc(pen, dotRect, 110, 280);
                }
                break;
            default:
                using (var pen = new Pen(p.BorderStrong, Draw.ScaleF(this, 1.4f))) e.Graphics.DrawEllipse(pen, dotRect);
                break;
        }
        TextRenderer.DrawText(e.Graphics, label, Font, textRect, textColor,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        if (Focused && ShowFocusCues) Draw.FocusRing(e.Graphics, bounds, radius, p.FocusRing);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}
