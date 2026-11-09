using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui.Controls;

internal sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        Size = new Size(40, 22);
        Text = "";
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Theme.Bind(this);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var radius = Height / 2;
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        Draw.FillRounded(e.Graphics, track, radius, Checked ? p.Accent : p.BorderStrong);
        var knob = Height - Draw.Scale(this, 4);
        var x = Checked ? Width - knob - Draw.Scale(this, 2) : Draw.Scale(this, 2);
        using (var shadow = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
            e.Graphics.FillEllipse(shadow, x, Draw.Scale(this, 3), knob, knob);
        using (var brush = new SolidBrush(Color.White))
            e.Graphics.FillEllipse(brush, x, Draw.Scale(this, 2), knob, knob);
        if (Focused && ShowFocusCues) Draw.FocusRing(e.Graphics, track, radius, p.FocusRing);
    }
}

internal sealed class SegmentedControl : Control
{
    private int selectedIndex;
    private int hoverIndex = -1;

    public string[] Items { get; set; } = Array.Empty<string>();
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(0, Items.Length - 1));
            if (next == selectedIndex) return;
            selectedIndex = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public event EventHandler? SelectedIndexChanged;

    public SegmentedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        Height = 26;
        TabStop = true;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Grouping;
        Font = Typography.Caption;
        Theme.Bind(this);
    }

    public int MeasureWidth()
    {
        var total = 0;
        foreach (var item in Items)
            total += TextRenderer.MeasureText(item, Font, new Size(2000, 100), TextFormatFlags.NoPrefix).Width + Draw.Scale(this, 18);
        return total + Draw.Scale(this, 4);
    }

    private List<Rectangle> SegmentBounds()
    {
        var result = new List<Rectangle>();
        if (Items.Length == 0) return result;
        var widths = Items.Select(item =>
            TextRenderer.MeasureText(item, Font, new Size(2000, 100), TextFormatFlags.NoPrefix).Width + Draw.Scale(this, 18)).ToArray();
        var total = widths.Sum();
        var available = Width - Draw.Scale(this, 4);
        var extra = Math.Max(0, (available - total) / Items.Length);
        var x = Draw.Scale(this, 2);
        foreach (var width in widths)
        {
            var w = width + extra;
            result.Add(new Rectangle(x, Draw.Scale(this, 2), w, Height - Draw.Scale(this, 4)));
            x += w;
        }
        return result;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var radius = Draw.Scale(this, 7);
        Draw.FillRounded(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), radius,
            Draw.Blend(p.Background, p.TextPrimary, 0.05));
        var bounds = SegmentBounds();
        for (var i = 0; i < bounds.Count; i++)
        {
            var rect = bounds[i];
            if (i == SelectedIndex)
            {
                Draw.FillRounded(e.Graphics, rect, Draw.Scale(this, 5), p.Surface);
                Draw.StrokeRounded(e.Graphics, rect, Draw.Scale(this, 5), p.Border);
            }
            var color = i == SelectedIndex ? p.TextPrimary : i == hoverIndex ? p.TextPrimary : p.TextSecondary;
            TextRenderer.DrawText(e.Graphics, Items[i], Font, rect, color,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused && ShowFocusCues) Draw.FocusRing(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), radius, p.FocusRing);
    }

    private int HitTest(Point point)
    {
        var bounds = SegmentBounds();
        for (var i = 0; i < bounds.Count; i++) if (bounds[i].Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit != hoverIndex) { hoverIndex = hit; Invalidate(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var hit = HitTest(e.Location);
            if (hit >= 0) SelectedIndex = hit;
        }
        base.OnMouseDown(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex--; e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SelectedIndex++; e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
}

internal class TextField : RoundedPanel
{
    private readonly TextBox input = new()
    {
        BorderStyle = BorderStyle.None,
        Font = Typography.Body,
        BackColor = Color.White,
        ForeColor = Color.Black
    };

    public TextField()
    {
        Height = 36;
        Radius = 8;
        Controls.Add(input);
        input.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Theme.Changed += ApplyTheme;
        ApplyTheme();
    }

    [AllowNull]
    public override string Text
    {
        get => input.Text;
        set => input.Text = value ?? "";
    }

    public bool ReadOnly
    {
        get => input.ReadOnly;
        set => input.ReadOnly = value;
    }

    public TextBox InnerBox => input;

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        var p = Theme.Current;
        input.BackColor = p.Surface;
        input.ForeColor = input.ReadOnly ? p.TextSecondary : p.TextPrimary;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = Draw.Scale(this, 12);
        var height = input.PreferredHeight;
        input.SetBounds(pad, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - pad * 2), height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}

internal sealed class SearchBox : RoundedPanel
{
    private readonly TextBox input = new()
    {
        BorderStyle = BorderStyle.None,
        Font = Typography.Caption,
        PlaceholderText = "搜索日志",
        AccessibleName = "搜索日志",
        BackColor = Color.White,
        ForeColor = Color.Black
    };
    private readonly ThemedIconButton clear = new()
    {
        Glyph = Icons.Close,
        GlyphSize = 12,
        Size = new Size(22, 22),
        Visible = false,
        TabStop = false
    };

    public SearchBox()
    {
        Height = 28;
        Radius = 7;
        Controls.Add(input);
        Controls.Add(clear);
        input.TextChanged += (_, _) =>
        {
            clear.Visible = input.TextLength > 0;
            OnTextChanged(EventArgs.Empty);
        };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { input.Clear(); e.SuppressKeyPress = true; }
        };
        clear.Click += (_, _) => { input.Clear(); input.Focus(); };
        clear.Backdrop = Theme.Current.Surface;
        Theme.Changed += ApplyTheme;
        ApplyTheme();
    }

    [AllowNull]
    public override string Text
    {
        get => input.Text;
        set => input.Text = value ?? "";
    }

    public void FocusInput() => input.Focus();

    public void Clear() => input.Clear();

    private void ApplyTheme()
    {
        if (IsDisposed) return;
        var p = Theme.Current;
        input.BackColor = p.Surface;
        input.ForeColor = p.TextPrimary;
        clear.Backdrop = p.Surface;
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var left = Draw.Scale(this, 26);
        var right = Draw.Scale(this, 26);
        var height = input.PreferredHeight;
        input.SetBounds(left, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - left - right), height);
        clear.Location = new Point(Math.Max(left, Width - clear.Width - Draw.Scale(this, 3)), (Height - clear.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var icon = Draw.Scale(this, 14);
        Icons.Draw(e.Graphics, Icons.Search, new Rectangle(Draw.Scale(this, 7), (Height - icon) / 2, icon, icon), p.TextTertiary, icon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}

internal sealed class AddressPicker : Control
{
    private bool hover, pressed;

    public string SelectedText { get; set; } = "";

    public AddressPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = Typography.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = "访问地址";
        Theme.Bind(this);
    }

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PickerAccessibleObject(this);

    private sealed class PickerAccessibleObject : ControlAccessibleObject
    {
        private readonly AddressPicker owner;
        public PickerAccessibleObject(AddressPicker owner) : base(owner) { this.owner = owner; }
        public override string? Name => string.IsNullOrWhiteSpace(owner.AccessibleName) ? base.Name : owner.AccessibleName;
        public override string? DefaultAction => "展开地址列表";
        public override void DoDefaultAction() => owner.PerformClick();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Draw.Scale(this, 8);
        Draw.FillRounded(e.Graphics, bounds, radius, Enabled ? p.Surface : p.SurfaceMuted);
        Draw.StrokeRounded(e.Graphics, bounds, radius, !Enabled ? p.Border : hover ? p.BorderStrong : p.Border);
        var color = Enabled ? (SelectedText.Length == 0 ? p.TextTertiary : p.TextPrimary) : p.TextDisabled;
        var text = SelectedText.Length == 0 ? "服务未运行" : SelectedText;
        var pad = Draw.Scale(this, 12);
        var chevron = Draw.Scale(this, 16);
        TextRenderer.DrawText(e.Graphics, text, Font,
            new Rectangle(pad, 0, Math.Max(0, Width - pad - chevron - Draw.Scale(this, 8)), Height), color,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        Icons.Draw(e.Graphics, Icons.ChevronDown,
            new Rectangle(Width - chevron - Draw.Scale(this, 10), (Height - chevron) / 2, chevron, chevron),
            Enabled ? p.TextTertiary : p.TextDisabled, chevron);
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
        if (e.KeyCode == Keys.Enter && Enabled) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }
    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
}
