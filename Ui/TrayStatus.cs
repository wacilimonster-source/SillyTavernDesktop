using System.Drawing;

namespace SillyTavernTray.Ui;

/// <summary>
/// Fixed status colors for the tray icon dot. The tray lives on the Windows taskbar,
/// whose coloring does not follow the app theme, so the dot uses saturated colors that
/// stay readable on both light and dark taskbars (LM Studio/Ollama tray convention:
/// green = running, amber = busy, red = trouble, gray = stopped). The white outline
/// drawn around the dot by the form guarantees contrast in both directions.
/// </summary>
internal static class TrayStatus
{
    public static Color ColorFor(StatusTone tone) => tone switch
    {
        StatusTone.Running => Color.FromArgb(63, 185, 80),    // green — service is up
        StatusTone.Busy => Color.FromArgb(210, 153, 34),      // amber — starting/stopping/maintenance
        StatusTone.Error => Color.FromArgb(248, 81, 73),      // red — failed/occupied/config error
        _ => Color.FromArgb(139, 148, 158)                    // gray — stopped/unconfigured
    };
}
