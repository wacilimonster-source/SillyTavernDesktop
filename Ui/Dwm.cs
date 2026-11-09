using System.Runtime.InteropServices;

namespace SillyTavernTray.Ui;

internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    public static void Apply(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            var dark = Theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
