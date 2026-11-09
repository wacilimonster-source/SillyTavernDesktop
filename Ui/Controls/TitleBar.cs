using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SillyTavernTray.Ui.Controls;

internal sealed class TitleBar : Panel
{
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    private readonly ThemedIconButton settingsButton;
    private readonly ThemedIconButton minimizeButton;
    private readonly ThemedIconButton closeButton;
    private readonly ToolTip tip = new() { InitialDelay = 500, ReshowDelay = 100 };

    public event EventHandler? SettingsClicked;
    public event EventHandler? MinimizeClicked;
    public event EventHandler? CloseClicked;

    public Icon? AppIcon { get; set; }
    public string Title { get; set; } = "SillyTavern 管理器";
    public string Version { get; set; } = "";
    public bool ShowSettingsButton { get => settingsButton.Visible; set => settingsButton.Visible = value; }
    public bool ShowMinimizeButton { get => minimizeButton.Visible; set => minimizeButton.Visible = value; }
    public bool ShowCloseButton { get => closeButton.Visible; set => closeButton.Visible = value; }

    public TitleBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Top;
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.Pane;
        AccessibleName = "标题栏";

        settingsButton = CreateButton(Icons.Settings, "设置 (Ctrl+,)", () => SettingsClicked?.Invoke(this, EventArgs.Empty));
        minimizeButton = CreateButton(Icons.Minimize, "收起到托盘", () => MinimizeClicked?.Invoke(this, EventArgs.Empty));
        closeButton = CreateButton(Icons.Close, "关闭到托盘", () => CloseClicked?.Invoke(this, EventArgs.Empty));
        closeButton.DangerHover = true;
        Controls.AddRange([settingsButton, minimizeButton, closeButton]);
        Height = 40;
        Theme.Bind(this);
    }

    private ThemedIconButton CreateButton(string glyph, string tooltip, Action click)
    {
        var button = new ThemedIconButton { Glyph = glyph, GlyphSize = 16, Size = new Size(30, 30), Text = tooltip };
        button.AccessibleName = tooltip;
        button.Click += (_, _) => click();
        tip.SetToolTip(button, tooltip);
        return button;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        var y = (Height - closeButton.Height) / 2;
        var x = Width - Draw.Scale(this, 8) - closeButton.Width;
        closeButton.Location = new Point(x, y);
        minimizeButton.Location = new Point(x - Draw.Scale(this, 32), y);
        settingsButton.Location = new Point(x - Draw.Scale(this, 64), y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var p = Theme.Current;
        using (var brush = new SolidBrush(p.Background)) e.Graphics.FillRectangle(brush, ClientRectangle);
        using (var pen = new Pen(p.Border)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);

        var iconSize = Draw.Scale(this, 20);
        var x = Draw.Scale(this, 14);
        if (AppIcon is not null)
        {
            try { e.Graphics.DrawIcon(AppIcon, new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize)); }
            catch { }
        }
        x += iconSize + Draw.Scale(this, 9);
        var textHeight = TextRenderer.MeasureText(Title, Typography.Title).Height;
        TextRenderer.DrawText(e.Graphics, Title, Typography.Title,
            new Rectangle(x, (Height - textHeight) / 2, Math.Max(0, Width - x - Draw.Scale(this, 120)), textHeight),
            p.TextPrimary, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);
        x += TextRenderer.MeasureText(Title, Typography.Title, new Size(4000, 100), TextFormatFlags.NoPrefix).Width + Draw.Scale(this, 8);

        if (!string.IsNullOrEmpty(Version))
        {
            var versionSize = TextRenderer.MeasureText(Version, Typography.Caption, new Size(4000, 100), TextFormatFlags.NoPrefix);
            var pillWidth = versionSize.Width + Draw.Scale(this, 14);
            var pillHeight = Draw.Scale(this, 18);
            var pillX = x;
            var settingsX = settingsButton.Visible ? settingsButton.Left : Width;
            if (pillX + pillWidth < settingsX - Draw.Scale(this, 8))
            {
                var pill = new Rectangle(pillX, (Height - pillHeight) / 2, pillWidth, pillHeight);
                Draw.FillRounded(e.Graphics, pill, pillHeight / 2, p.Surface);
                Draw.StrokeRounded(e.Graphics, pill, pillHeight / 2, p.Border);
                TextRenderer.DrawText(e.Graphics, Version, Typography.Caption, pill, p.TextTertiary,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var form = FindForm();
        if (form is null) return;
        ReleaseCapture();
        SendMessage(form.Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing) tip.Dispose();
        base.Dispose(disposing);
    }
}
