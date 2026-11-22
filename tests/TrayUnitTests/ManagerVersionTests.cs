using Xunit;
using SillyTavernTray;

namespace TrayUnitTests;

public class ManagerVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("v10.20.30", 10, 20, 30)]
    public void TryParse_AcceptsReleaseTags(string text, int major, int minor, int build)
    {
        Assert.True(ManagerVersion.TryParse(text, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3-beta")]
    [InlineData("version 1.2.3")]
    [InlineData("vv1.2.3")]
    [InlineData("v1.2.3 ")]
    public void TryParse_RejectsNonReleaseText(string text)
    {
        Assert.False(ManagerVersion.TryParse(text, out _));
    }

    [Fact]
    public void Normalize_DropsRevisionAndNegativeBuild()
    {
        var dropped = ManagerVersion.Normalize(new Version(1, 2, 3, 99));
        Assert.Equal((1, 2, 3), (dropped.Major, dropped.Minor, dropped.Build));

        // A two-part version string parses with Build = -1 (unspecified); Normalize floors it to 0.
        var zeroed = ManagerVersion.Normalize(new Version("1.2"));
        Assert.Equal((1, 2, 0), (zeroed.Major, zeroed.Minor, zeroed.Build));
    }

    [Fact]
    public void Distribution_ConstantsAreConsistent()
    {
        Assert.Equal("https://api.github.com/repos/wacilimonster-source/SillyTavernDesktop/releases/latest", ManagerDistribution.LatestApiUrl);
        Assert.Equal("/wacilimonster-source/SillyTavernDesktop/releases/download/v1.2.3/SillyTavernTray-win-x64.exe",
            ManagerDistribution.DownloadPath("v1.2.3"));
        Assert.Equal("https://github.com/wacilimonster-source/SillyTavernDesktop", ManagerDistribution.ProjectUrl);
    }
}
