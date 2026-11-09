using System.Runtime.InteropServices;

namespace SillyTavernTray.Ui.Controls;

internal sealed class LogRichTextBox : RichTextBox
{
    public event Action? ScrolledUp;

    public LogRichTextBox()
    {
        BorderStyle = BorderStyle.None;
        ReadOnly = true;
        DetectUrls = false;
        WordWrap = true;
        ScrollBars = RichTextBoxScrollBars.Vertical;
        Font = Typography.Mono;
        HideSelection = false;
        Theme.Changed += ApplyTheme;
        ApplyTheme();
    }

    public void ApplyTheme()
    {
        if (IsDisposed) return;
        var p = Theme.Current;
        BackColor = p.Surface;
        ForeColor = p.TextPrimary;
        ApplyScrollbarTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScrollbarTheme();
    }

    private void ApplyScrollbarTheme()
    {
        if (!IsHandleCreated) return;
        try
        {
            var theme = Theme.IsDark ? "DarkMode_Explorer" : "Explorer";
            SetWindowTheme(Handle, theme, null);
            EnumChildWindows(Handle, (child, _) => { SetWindowTheme(child, theme, null); return true; }, IntPtr.Zero);
        }
        catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x020A) // WM_MOUSEWHEEL
        {
            if ((short)((long)m.WParam >> 16) > 0) ScrolledUp?.Invoke();
        }
        else if (m.Msg == 0x0115) // WM_VSCROLL
        {
            var code = (int)(long)m.WParam & 0xFFFF;
            if (code is 0 or 2) ScrolledUp?.Invoke(); // SB_LINEUP / SB_PAGEUP
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? applicationName, string? idList);

    protected override void Dispose(bool disposing)
    {
        if (disposing) Theme.Changed -= ApplyTheme;
        base.Dispose(disposing);
    }
}
