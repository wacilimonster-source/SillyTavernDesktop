using Xunit;
using System.Drawing;
using SillyTavernTray.Ui;

namespace TrayUnitTests;

public class TrayStatusTests
{
    [Fact]
    public void EachToneHasADistinctFixedColor()
    {
        var colors = new[]
        {
            TrayStatus.ColorFor(StatusTone.Running),
            TrayStatus.ColorFor(StatusTone.Busy),
            TrayStatus.ColorFor(StatusTone.Error),
            TrayStatus.ColorFor(StatusTone.Stopped)
        };
        Assert.Equal(colors.Length, colors.Distinct().Count());
    }

    [Fact]
    public void Running_IsGreen()
    {
        Assert.Equal(Color.FromArgb(63, 185, 80), TrayStatus.ColorFor(StatusTone.Running));
    }

    [Fact]
    public void Busy_IsAmber()
    {
        Assert.Equal(Color.FromArgb(210, 153, 34), TrayStatus.ColorFor(StatusTone.Busy));
    }

    [Fact]
    public void Error_IsRed()
    {
        Assert.Equal(Color.FromArgb(248, 81, 73), TrayStatus.ColorFor(StatusTone.Error));
    }

    [Fact]
    public void Stopped_IsNeutralGray()
    {
        Assert.Equal(Color.FromArgb(139, 148, 158), TrayStatus.ColorFor(StatusTone.Stopped));
    }

    [Fact]
    public void ColorsAreSaturatedEnoughForBothTaskbarModes()
    {
        // A dot that fades toward the taskbar background is invisible; every status color
        // must stay far enough from both white and near-black backgrounds.
        foreach (var tone in Enum.GetValues<StatusTone>())
        {
            var color = TrayStatus.ColorFor(tone);
            var brightness = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            Assert.InRange(brightness, 60, 200);
        }
    }
}
