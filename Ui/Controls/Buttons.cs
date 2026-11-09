using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui.Controls;

internal enum ButtonVariant { Accent, DangerAccent, Secondary, Quiet }

internal class ThemedButton : Control, IButtonControl
{
    private bool hover, pressed;

    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;
    public string? IconGlyph { get; set; }
    public string? Caption { get; set; }
    public Color? IconColorOverride { get; set; }
    public bool DangerHover { get; set; }
    public Color? Backdrop { get; set; }
    public int PreferredHeight { get; set; } = 32;
    public DialogResult DialogResult { get; set; } = DialogResult.None;

    public ThemedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Current.TextPrimary;
        Font = Typography.Button;
        AutoSize = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Theme.Bind(this);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var textSize = TextRenderer.MeasureText(Caption ?? Text ?? "", Font, new Size(8000, 200),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var icon = string.IsNullOrEmpty(IconGlyph) ? 0 : Draw.Scale(this, 16) + Draw.Scale(this, 6);
        var padding = Draw.Scale(this, Variant == ButtonVariant.Quiet ? 18 : 28);
        return new Size(textSize.Width + icon + padding, Draw.Scale(this, PreferredHeight));
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var board = new SolidBrush(Backdrop ?? Theme.Current.Background))
            e.Graphics.FillRectangle(board, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var scale = DeviceDpi / 96f;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = (int)Math.Round((Variant == ButtonVariant.Quiet ? 6 : 8) * scale);

        var fill = Backdrop ?? (Variant == ButtonVariant.Quiet ? p.Background : p.Surface);
        Color border = Color.Transparent;
        Color textColor;

        if (!Enabled)
        {
            fill = Variant is ButtonVariant.Accent or ButtonVariant.DangerAccent or ButtonVariant.Secondary
                ? p.SurfaceMuted
                : fill;
            border = Variant == ButtonVariant.Secondary ? p.Border : Color.Transparent;
            textColor = p.TextDisabled;
        }
        else
        {
            switch (Variant)
            {
                case ButtonVariant.Accent:
                    fill = pressed ? p.AccentPressed : hover ? p.AccentHover : p.Accent;
                    textColor = p.OnAccent;
                    break;
                case ButtonVariant.DangerAccent:
                    fill = Hover(p.Danger);
                    textColor = p.OnDanger;
                    break;
                case ButtonVariant.Secondary:
                    fill = hover ? p.SurfaceMuted : p.Surface;
                    border = hover ? p.BorderStrong : p.Border;
                    textColor = p.TextPrimary;
                    break;
                default:
                    if (hover && DangerHover) fill = p.DangerSoft;
                    else if (hover) fill = Draw.Blend(Backdrop ?? p.Background, p.TextPrimary, 0.06);
                    textColor = hover && DangerHover ? p.DangerText : p.TextSecondary;
                    break;
            }
        }

        Draw.FillRounded(e.Graphics, bounds, radius, fill);
        if (border != Color.Transparent) Draw.StrokeRounded(e.Graphics, bounds, radius, border);

        var iconColor = IconColorOverride ?? (Variant is ButtonVariant.Accent or ButtonVariant.DangerAccent
            ? textColor
            : (Enabled ? (hover && DangerHover ? p.DangerText : p.TextSecondary) : p.TextDisabled));
        var caption = Caption ?? Text ?? "";
        var textSize = TextRenderer.MeasureText(caption, Font, new Size(8000, 200),
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var iconPx = Draw.Scale(this, 16);
        var gap = Draw.Scale(this, 6);
        var iconWidth = string.IsNullOrEmpty(IconGlyph) ? 0 : iconPx + gap;
        var total = iconWidth + textSize.Width;
        var x = Math.Max(0, (Width - total) / 2);
        var y = Math.Max(0, (Height - textSize.Height) / 2);
        if (!string.IsNullOrEmpty(IconGlyph))
        {
            Icons.Draw(e.Graphics, IconGlyph, new Rectangle(x, (Height - iconPx) / 2, iconPx, iconPx), iconColor, iconPx);
            x += iconWidth;
        }
        TextRenderer.DrawText(e.Graphics, caption, Font, new Rectangle(x, y, Math.Max(0, Width - x), textSize.Height),
            textColor, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);

        if (Focused && ShowFocusCues) Draw.FocusRing(e.Graphics, bounds, radius, p.FocusRing);
    }

    private Color Hover(Color color) => Theme.IsDark ? Draw.Blend(color, Color.White, 0.12) : Draw.Blend(color, Color.Black, 0.08);

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    public void NotifyDefault(bool value) => Invalidate();

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibleObject(this);

    private sealed class ButtonAccessibleObject : ControlAccessibleObject
    {
        private readonly ThemedButton owner;
        public ButtonAccessibleObject(ThemedButton owner) : base(owner) { this.owner = owner; }
        public override string? Name => string.IsNullOrWhiteSpace(owner.AccessibleName) ? base.Name : owner.AccessibleName;
        public override string? DefaultAction => "按下";
        public override void DoDefaultAction() => owner.PerformClick();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled) { pressed = true; Capture = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pressed)
        {
            pressed = false;
            Capture = false;
            Invalidate();
            if (ClientRectangle.Contains(e.Location) && Enabled) OnClick(EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !pressed) { pressed = true; Invalidate(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && Enabled) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && pressed)
        {
            pressed = false;
            Invalidate();
            if (Enabled) OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}

internal sealed class ThemedIconButton : Control
{
    private bool hover, pressed;

    public string Glyph { get; set; } = Icons.Settings;
    public int GlyphSize { get; set; } = 16;
    public bool DangerHover { get; set; }
    public Color? Backdrop { get; set; }

    public ThemedIconButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Size = new Size(30, 30);
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.PushButton;
        Theme.Bind(this);
    }

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new IconAccessibleObject(this);

    private sealed class IconAccessibleObject : ControlAccessibleObject
    {
        private readonly ThemedIconButton owner;
        public IconAccessibleObject(ThemedIconButton owner) : base(owner) { this.owner = owner; }
        public override string? Name => string.IsNullOrWhiteSpace(owner.AccessibleName) ? base.Name : owner.AccessibleName;
        public override string? DefaultAction => "按下";
        public override void DoDefaultAction() => owner.PerformClick();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var backdrop = Backdrop ?? Theme.Current.Background;
        using (var board = new SolidBrush(backdrop))
            e.Graphics.FillRectangle(board, ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Draw.Scale(this, 6);
        var fill = hover ? (DangerHover ? p.DangerSoft : Draw.Blend(backdrop, p.TextPrimary, 0.08)) : backdrop;
        if (pressed && hover) fill = Draw.Blend(fill, p.TextPrimary, 0.06);
        Draw.FillRounded(e.Graphics, bounds, radius, fill);
        var color = !Enabled ? p.TextDisabled : hover && DangerHover ? p.DangerText : hover ? p.TextPrimary : p.TextSecondary;
        var iconPx = Draw.Scale(this, GlyphSize);
        Icons.Draw(e.Graphics, Glyph, new Rectangle((Width - iconPx) / 2, (Height - iconPx) / 2, iconPx, iconPx), color, iconPx);
        if (Focused && ShowFocusCues) Draw.FocusRing(e.Graphics, bounds, radius, p.FocusRing);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && Enabled) { pressed = true; Capture = true; Invalidate(); }
        base.OnMouseDown(e);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && pressed)
        {
            pressed = false;
            Capture = false;
            Invalidate();
            if (ClientRectangle.Contains(e.Location) && Enabled) OnClick(EventArgs.Empty);
        }
        base.OnMouseUp(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && !pressed) { pressed = true; Invalidate(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && Enabled) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && pressed)
        {
            pressed = false;
            Invalidate();
            if (Enabled) OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
