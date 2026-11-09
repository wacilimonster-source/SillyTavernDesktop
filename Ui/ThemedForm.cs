using System.Runtime.InteropServices;

namespace SillyTavernTray.Ui;

internal class ThemedForm : Form
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;

    private readonly BorderlessResizeFilter? resizeFilter;

    public bool Resizable { get; }

    public ThemedForm(bool resizable = true)
    {
        Resizable = resizable;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Current.Border;
        Padding = new Padding(1);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        if (resizable)
        {
            resizeFilter = new BorderlessResizeFilter(this, 6);
            Application.AddMessageFilter(resizeFilter);
        }
        Theme.Changed += OnThemeChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwm();
    }

    private void ApplyDwm() => Dwm.Apply(Handle);

    protected virtual void ApplyTheme() { }

    private void OnThemeChanged()
    {
        if (IsDisposed) return;
        BackColor = Theme.Current.Border;
        ApplyDwm();
        ApplyTheme();
        Invalidate(true);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SETTINGCHANGE || m.Msg == WM_THEMECHANGED) Theme.Refresh();
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Theme.Current.Border);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            if (resizeFilter is not null) Application.RemoveMessageFilter(resizeFilter);
        }
        base.Dispose(disposing);
    }
}

internal sealed class BorderlessResizeFilter : IMessageFilter
{
    private const int WM_NCHITTEST = 0x84;
    private readonly Form form;
    private readonly int grip;

    public BorderlessResizeFilter(Form form, int grip)
    {
        this.form = form;
        this.grip = grip;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_NCHITTEST || !form.IsHandleCreated || !form.Visible) return false;
        var control = Control.FromHandle(m.HWnd);
        if (control is null) return false;
        if (control != form && !form.Contains(control)) return false;
        var screen = new Point((short)(long)m.LParam, (short)((long)m.LParam >> 16));
        var bounds = form.Bounds;
        var gripPx = Math.Max(4, (int)Math.Round(grip * form.DeviceDpi / 96.0));
        if (screen.X < bounds.Left - gripPx || screen.X > bounds.Right + gripPx ||
            screen.Y < bounds.Top - gripPx || screen.Y > bounds.Bottom + gripPx) return false;
        var left = screen.X <= bounds.Left + gripPx;
        var right = screen.X >= bounds.Right - gripPx;
        var top = screen.Y <= bounds.Top + gripPx;
        var bottom = screen.Y >= bounds.Bottom - gripPx;
        if (!left && !right && !top && !bottom) return false;
        var hit = left ? (top ? 13 : bottom ? 16 : 10)
            : right ? (top ? 14 : bottom ? 17 : 11)
            : top ? 12 : 15;
        m.Result = (IntPtr)hit;
        return true;
    }
}
