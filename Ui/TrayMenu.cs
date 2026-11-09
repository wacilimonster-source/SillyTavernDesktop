using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui;

internal sealed class TrayHeaderPanel : Control
{
    public string Status { get; set; } = "运行中 · 端口 8000";
    public string Subtext { get; set; } = "";
    public Color DotColor { get; set; } = Color.Gray;

    public TrayHeaderPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 248;
        Height = 46;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = "SillyTavern 状态";
        Theme.Bind(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        using (var brush = new SolidBrush(p.Surface)) e.Graphics.FillRectangle(brush, ClientRectangle);
        var dot = Draw.Scale(this, 8);
        var dx = Draw.Scale(this, 14);
        var dy = Draw.Scale(this, 16);
        using (var brush = new SolidBrush(DotColor)) e.Graphics.FillEllipse(brush, dx, dy, dot, dot);
        var textX = dx + dot + Draw.Scale(this, 8);
        TextRenderer.DrawText(e.Graphics, Status, Typography.ButtonBold,
            new Rectangle(textX, Draw.Scale(this, 7), Width - textX - 8, Draw.Scale(this, 20)), p.TextPrimary,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, Subtext, Typography.Caption,
            new Rectangle(textX, Draw.Scale(this, 25), Width - textX - 8, Draw.Scale(this, 16)), p.TextTertiary,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    public TrayMenuRenderer() : base(new TrayColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Surface);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var p = Theme.Current;
        using var pen = new Pen(p.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Theme.Current.Surface);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected) return;
        var p = Theme.Current;
        var danger = e.Item.Tag as string == "danger";
        var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        Draw.FillRounded(e.Graphics, bounds, 6, danger ? p.DangerSoft : p.AccentSoft);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = Theme.Current;
        var color = !e.Item.Enabled ? p.TextDisabled
            : e.Item.Tag as string == "danger" ? p.DangerText
            : p.TextPrimary;
        var rect = e.TextRectangle;
        if (rect.Width <= 0 || rect.Height <= 0)
            rect = new Rectangle(e.Item.Bounds.X + 34, e.Item.Bounds.Y, e.Item.Bounds.Width - 40, e.Item.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, e.Text, e.Item.Font, rect, color,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var p = Theme.Current;
        var y = e.Item.Height / 2;
        using var pen = new Pen(p.Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image is null) return;
        var rect = new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y, e.ImageRectangle.Width, e.ImageRectangle.Height);
        e.Graphics.DrawImage(e.Image, rect);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { }
}

internal sealed class TrayColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Current.Surface;
    public override Color ImageMarginGradientBegin => Theme.Current.Surface;
    public override Color ImageMarginGradientMiddle => Theme.Current.Surface;
    public override Color ImageMarginGradientEnd => Theme.Current.Surface;
    public override Color MenuBorder => Theme.Current.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => Theme.Current.AccentSoft;
    public override Color MenuItemSelectedGradientBegin => Theme.Current.AccentSoft;
    public override Color MenuItemSelectedGradientEnd => Theme.Current.AccentSoft;
    public override Color SeparatorDark => Theme.Current.Border;
    public override Color SeparatorLight => Theme.Current.Border;
}
