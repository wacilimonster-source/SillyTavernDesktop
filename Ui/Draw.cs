using System.Drawing.Drawing2D;

namespace SillyTavernTray.Ui;

internal static class Draw
{
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) { path.AddRectangle(bounds); return path; }
        var r = Math.Max(0, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        if (r == 0) { path.AddRectangle(bounds); return path; }
        var d = r * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)Math.Round(a.A + (b.A - a.A) * t),
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }

    public static void FillRounded(Graphics g, Rectangle bounds, int radius, Color fill)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    public static void StrokeRounded(Graphics g, Rectangle bounds, int radius, Color color, float width = 1f)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = RoundedRect(bounds, radius);
        using var pen = new Pen(color, width) { Alignment = PenAlignment.Inset };
        g.DrawPath(pen, path);
    }

    public static void FocusRing(Graphics g, Rectangle bounds, int radius, Color color)
    {
        var inset = new Rectangle(bounds.X + 2, bounds.Y + 2, bounds.Width - 4, bounds.Height - 4);
        StrokeRounded(g, inset, Math.Max(2, radius - 2), color, 2f);
    }

    public static int Scale(Control control, int logical) =>
        (int)Math.Round(logical * control.DeviceDpi / 96.0);

    public static float ScaleF(Control control, float logical) =>
        (float)(logical * control.DeviceDpi / 96.0);
}

internal static class Icons
{
    public const string Play = "\uE768";
    public const string Stop = "\uE71A";
    public const string Restart = "\uE72C";
    public const string OpenExternal = "\uE8A7";
    public const string Copy = "\uE8C8";
    public const string Search = "\uE721";
    public const string Pause = "\uE769";
    public const string Settings = "\uE713";
    public const string Folder = "\uE8B7";
    public const string Download = "\uE896";
    public const string Repair = "\uE90F";
    public const string Power = "\uE7E8";
    public const string Check = "\uE73E";
    public const string Warning = "\uE7BA";
    public const string Error = "\uEA39";
    public const string ChevronDown = "\uE70D";
    public const string More = "\uE712";
    public const string History = "\uE81C";
    public const string Minimize = "\uE921";
    public const string Close = "\uE8BB";
    public const string AppWindow = "\uE737";
    public const string Down = "\uE74B";
    public const string Info = "\uE946";

    public const TextFormatFlags GlyphFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;

    public static void Draw(Graphics g, string glyph, Rectangle bounds, Color color, int pixelSize)
    {
        TextRenderer.DrawText(g, glyph, Typography.IconFont(pixelSize), bounds, color, GlyphFlags);
    }

    public static Bitmap ToBitmap(string glyph, int pixelSize, Color color)
    {
        var bitmap = new Bitmap(pixelSize, pixelSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Draw(g, glyph, new Rectangle(0, 0, pixelSize, pixelSize), color, pixelSize);
        return bitmap;
    }
}
