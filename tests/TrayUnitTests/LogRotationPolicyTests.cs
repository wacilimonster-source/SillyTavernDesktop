using Xunit;
using SillyTavernTray;

namespace TrayUnitTests;

public class LogRotationPolicyTests
{
    private static readonly DateTime Day = new(2026, 11, 22, 15, 30, 45);

    [Fact]
    public void PreferredArchiveName_IsDaily()
    {
        Assert.Equal("tray-20261122.log", LogRotationPolicy.PreferredArchiveName(Day));
        Assert.Equal("tray-20261122.log", LogRotationPolicy.PreferredArchiveName(Day.AddHours(3))); // same day, different time
    }

    [Fact]
    public void ResolveArchiveName_UsesPreferredWhenFree()
    {
        Assert.Equal("tray-20261122.log", LogRotationPolicy.ResolveArchiveName(Day, ["tray-20261121.log"]));
    }

    [Fact]
    public void ResolveArchiveName_TimeSuffixWhenDailyNameTaken()
    {
        var existing = new[] { "tray.log", "tray-20261122.log", "tray-20261121.log" };
        Assert.Equal("tray-20261122-153045.log", LogRotationPolicy.ResolveArchiveName(Day, existing));
    }

    [Fact]
    public void ResolveArchiveName_SecondSuffixWhenBothTaken()
    {
        var existing = new[] { "tray-20261122.log", "tray-20261122-153045.log" };
        Assert.Equal("tray-20261122-153045-01.log", LogRotationPolicy.ResolveArchiveName(Day, existing));
    }

    [Fact]
    public void ExcessArchives_NoneWithinCap()
    {
        var files = Enumerable.Range(1, 10).Select(i => $"tray-202611{i:D2}.log").ToArray();
        Assert.Empty(LogRotationPolicy.ExcessArchives(files));
    }

    [Fact]
    public void ExcessArchives_DeletesOldestDated()
    {
        var files = Enumerable.Range(1, 12).Select(i => $"tray-202611{i:D2}.log").ToArray();
        var excess = LogRotationPolicy.ExcessArchives(files);
        Assert.Equal(["tray-20261101.log", "tray-20261102.log"], excess);
    }

    [Fact]
    public void ExcessArchives_LegacyNumericBackupsAgeOutFirst()
    {
        var files = new List<string> { "tray.log.1", "tray.log.2", "tray.log.3" };
        files.AddRange(Enumerable.Range(1, 9).Select(i => $"tray-202611{i:D2}.log"));
        var excess = LogRotationPolicy.ExcessArchives(files);
        Assert.Equal(["tray.log.1", "tray.log.2"], excess);
    }

    [Fact]
    public void ExcessArchives_CustomCap()
    {
        var files = new[] { "tray-20261121.log", "tray-20261122.log" };
        Assert.Equal(["tray-20261121.log"], LogRotationPolicy.ExcessArchives(files, 1));
    }
}
