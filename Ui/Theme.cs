using System.Drawing.Text;
using Microsoft.Win32;

namespace SillyTavernTray.Ui;

internal enum StatusTone { Stopped, Running, Busy, Error }

internal sealed class Palette
{
    public Color Background { get; init; }
    public Color Surface { get; init; }
    public Color SurfaceMuted { get; init; }
    public Color Border { get; init; }
    public Color BorderStrong { get; init; }
    public Color TextPrimary { get; init; }
    public Color TextSecondary { get; init; }
    public Color TextTertiary { get; init; }
    public Color TextDisabled { get; init; }
    public Color Accent { get; init; }
    public Color AccentHover { get; init; }
    public Color AccentPressed { get; init; }
    public Color AccentSoft { get; init; }
    public Color OnAccent { get; init; }
    public Color OnDanger { get; init; }
    public Color Success { get; init; }
    public Color SuccessText { get; init; }
    public Color SuccessSoft { get; init; }
    public Color Warning { get; init; }
    public Color WarningText { get; init; }
    public Color WarningSoft { get; init; }
    public Color Danger { get; init; }
    public Color DangerText { get; init; }
    public Color DangerSoft { get; init; }
    public Color Purple { get; init; }
    public Color FocusRing { get; init; }

    public Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Running => Success,
        StatusTone.Busy => Warning,
        StatusTone.Error => Danger,
        _ => TextTertiary
    };

    public Color ToneText(StatusTone tone) => tone switch
    {
        StatusTone.Running => SuccessText,
        StatusTone.Busy => WarningText,
        StatusTone.Error => DangerText,
        _ => TextSecondary
    };

    public Color ToneSurface(StatusTone tone) => tone switch
    {
        StatusTone.Running => SuccessSoft,
        StatusTone.Busy => WarningSoft,
        StatusTone.Error => DangerSoft,
        _ => SurfaceMuted
    };
}

internal static class Theme
{
    private static bool initialized;

    public static bool IsDark { get; private set; }
    public static Palette Current => IsDark ? Dark : Light;
    public static event Action? Changed;

    public static readonly Palette Light = new()
    {
        Background = Color.FromArgb(0xF5, 0xF6, 0xF8),
        Surface = Color.FromArgb(0xFF, 0xFF, 0xFF),
        SurfaceMuted = Color.FromArgb(0xFA, 0xFB, 0xFC),
        Border = Color.FromArgb(0xE3, 0xE6, 0xEB),
        BorderStrong = Color.FromArgb(0xCD, 0xD3, 0xDB),
        TextPrimary = Color.FromArgb(0x1C, 0x20, 0x24),
        TextSecondary = Color.FromArgb(0x5A, 0x63, 0x70),
        TextTertiary = Color.FromArgb(0x8B, 0x94, 0xA1),
        TextDisabled = Color.FromArgb(0xB6, 0xBC, 0xC5),
        Accent = Color.FromArgb(0x3B, 0x5B, 0xDB),
        AccentHover = Color.FromArgb(0x34, 0x51, 0xC7),
        AccentPressed = Color.FromArgb(0x2E, 0x47, 0xB0),
        AccentSoft = Color.FromArgb(0xED, 0xF1, 0xFD),
        OnAccent = Color.White,
        OnDanger = Color.White,
        Success = Color.FromArgb(0x2F, 0x9E, 0x63),
        SuccessText = Color.FromArgb(0x1E, 0x7A, 0x4A),
        SuccessSoft = Color.FromArgb(0xED, 0xF8, 0xF2),
        Warning = Color.FromArgb(0xB7, 0x79, 0x1F),
        WarningText = Color.FromArgb(0x8A, 0x5A, 0x12),
        WarningSoft = Color.FromArgb(0xFD, 0xF6, 0xE9),
        Danger = Color.FromArgb(0xC2, 0x45, 0x3D),
        DangerText = Color.FromArgb(0xA5, 0x34, 0x2C),
        DangerSoft = Color.FromArgb(0xFC, 0xEF, 0xEE),
        Purple = Color.FromArgb(0x7C, 0x3A, 0xED),
        FocusRing = Color.FromArgb(115, 0x3B, 0x5B, 0xDB)
    };

    public static readonly Palette Dark = new()
    {
        Background = Color.FromArgb(0x17, 0x19, 0x1D),
        Surface = Color.FromArgb(0x1F, 0x22, 0x27),
        SurfaceMuted = Color.FromArgb(0x23, 0x26, 0x2C),
        Border = Color.FromArgb(0x30, 0x34, 0x3B),
        BorderStrong = Color.FromArgb(0x3D, 0x42, 0x4B),
        TextPrimary = Color.FromArgb(0xE9, 0xEB, 0xEE),
        TextSecondary = Color.FromArgb(0xA6, 0xAD, 0xB8),
        TextTertiary = Color.FromArgb(0x7C, 0x85, 0x93),
        TextDisabled = Color.FromArgb(0x56, 0x5D, 0x68),
        Accent = Color.FromArgb(0x7C, 0x97, 0xFF),
        AccentHover = Color.FromArgb(0x8F, 0xA6, 0xFF),
        AccentPressed = Color.FromArgb(0x6B, 0x86, 0xEF),
        AccentSoft = Color.FromArgb(0x23, 0x2A, 0x44),
        OnAccent = Color.FromArgb(0x10, 0x14, 0x2B),
        OnDanger = Color.FromArgb(0x1C, 0x10, 0x10),
        Success = Color.FromArgb(0x4C, 0xC3, 0x8A),
        SuccessText = Color.FromArgb(0x6F, 0xD6, 0xA1),
        SuccessSoft = Color.FromArgb(0x1B, 0x27, 0x21),
        Warning = Color.FromArgb(0xE0, 0xA4, 0x4A),
        WarningText = Color.FromArgb(0xE7, 0xB4, 0x65),
        WarningSoft = Color.FromArgb(0x2A, 0x23, 0x18),
        Danger = Color.FromArgb(0xE5, 0x73, 0x6B),
        DangerText = Color.FromArgb(0xF0, 0x8C, 0x85),
        DangerSoft = Color.FromArgb(0x2C, 0x1E, 0x1D),
        Purple = Color.FromArgb(0xB7, 0x94, 0xF6),
        FocusRing = Color.FromArgb(128, 0x7C, 0x97, 0xFF)
    };

    public static void Initialize()
    {
        var dark = ResolveDark();
        if (initialized)
        {
            if (dark == IsDark) return;
            IsDark = dark;
            Changed?.Invoke();
            return;
        }
        IsDark = dark;
        initialized = true;
    }

    public static void Refresh()
    {
        if (!initialized) { Initialize(); return; }
        var dark = ResolveDark();
        if (dark == IsDark) return;
        IsDark = dark;
        Changed?.Invoke();
    }

    private static bool ResolveDark()
    {
        var forced = Environment.GetEnvironmentVariable("ST_TRAY_THEME");
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
        return ReadSystemDark();
    }

    public static void Bind(Control control)
    {
        void Handler() { if (!control.IsDisposed) control.Invalidate(true); }
        Changed += Handler;
        control.Disposed += (_, _) => Changed -= Handler;
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }
}

internal static class Typography
{
    public const string Family = "Microsoft YaHei UI";

    private static readonly string MonoFamilyName = ResolveMonoFamily();
    private static readonly string IconFamilyName = ResolveIconFamily();

    public static readonly Font Status = new(Family, 18f, FontStyle.Bold);
    public static readonly Font Title = new(Family, 12f, FontStyle.Bold);
    public static readonly Font Section = new(Family, 9.5f, FontStyle.Bold);
    public static readonly Font Body = new(Family, 9.5f);
    public static readonly Font Caption = new(Family, 8.5f);
    public static readonly Font Button = new(Family, 9.5f);
    public static readonly Font ButtonBold = new(Family, 9.5f, FontStyle.Bold);
    public static readonly Font Mono = new(MonoFamilyName, 9f);

    private static readonly Dictionary<int, Font> IconFonts = new();

    public static Font IconFont(int pixelSize)
    {
        lock (IconFonts)
        {
            if (!IconFonts.TryGetValue(pixelSize, out var font))
            {
                font = new Font(IconFamilyName, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
                IconFonts[pixelSize] = font;
            }
            return font;
        }
    }

    private static string ResolveMonoFamily()
    {
        try
        {
            using var collection = new InstalledFontCollection();
            foreach (var family in collection.Families)
                if (string.Equals(family.Name, "Cascadia Mono", StringComparison.OrdinalIgnoreCase)) return "Cascadia Mono";
        }
        catch { }
        return "Consolas";
    }

    private static string ResolveIconFamily()
    {
        try
        {
            using var collection = new InstalledFontCollection();
            var names = collection.Families.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("Segoe Fluent Icons")) return "Segoe Fluent Icons";
            if (names.Contains("Segoe MDL2 Assets")) return "Segoe MDL2 Assets";
        }
        catch { }
        return "Segoe UI Symbol";
    }
}
