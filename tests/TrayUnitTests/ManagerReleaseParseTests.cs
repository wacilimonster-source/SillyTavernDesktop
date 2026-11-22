using Xunit;
using System.Text.Json;
using SillyTavernTray;

namespace TrayUnitTests;

public class ManagerReleaseParseTests
{
    private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string ReleaseJson(string tag = "v1.2.3", long size = 123, string hash = Hash64,
        bool draft = false, bool prerelease = false, string? downloadUrl = null, string? assetName = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = new[] { new
            {
                name = assetName ?? ManagerDistribution.AssetName,
                size,
                digest = "sha256:" + hash,
                browser_download_url = downloadUrl ??
                    $"https://github.com/{ManagerDistribution.RepoOwner}/{ManagerDistribution.RepoName}/releases/download/{tag}/{ManagerDistribution.AssetName}"
            } }
        });

    private static ManagerRelease Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ManagerUpdateService.ParseRelease(document.RootElement);
    }

    [Fact]
    public void ValidRelease_Parses()
    {
        var release = Parse(ReleaseJson(tag: "v2.0.1", size: 4096));
        Assert.Equal(new Version(2, 0, 1), release.Version);
        Assert.Equal("v2.0.1", release.Tag);
        Assert.Equal(4096, release.Size);
        Assert.Equal(Hash64, release.Sha256);
        Assert.True(release.DownloadUrl.IsAbsoluteUri && release.DownloadUrl.Scheme == "https");
    }

    [Theory]
    [InlineData(true, false)]   // draft
    [InlineData(false, true)]   // prerelease
    public void UnpublishedRelease_IsRejected(bool draft, bool prerelease)
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(draft: draft, prerelease: prerelease)));
    }

    [Theory]
    [InlineData("v1.2")]                            // not a release version
    [InlineData("v1.2.3-beta")]                     // semver suffix is not a plain tag
    public void InvalidTag_IsRejected(string tag)
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(tag: tag)));
    }

    [Fact]
    public void ForeignHost_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(
            downloadUrl: $"https://attacker.invalid/{ManagerDistribution.RepoOwner}/{ManagerDistribution.RepoName}/releases/download/v1.2.3/{ManagerDistribution.AssetName}")));
    }

    [Fact]
    public void ForeignRepoPath_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(
            downloadUrl: "https://github.com/other-owner/other-repo/releases/download/v1.2.3/SillyTavernTray-win-x64.exe")));
    }

    [Fact]
    public void UrlWithQuery_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(
            downloadUrl: $"https://github.com/{ManagerDistribution.RepoOwner}/{ManagerDistribution.RepoName}/releases/download/v1.2.3/{ManagerDistribution.AssetName}?token=abc")));
    }

    [Fact]
    public void UrlWithUserInfo_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(
            downloadUrl: $"https://user@github.com/{ManagerDistribution.RepoOwner}/{ManagerDistribution.RepoName}/releases/download/v1.2.3/{ManagerDistribution.AssetName}")));
    }

    [Fact]
    public void WrongAssetName_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(assetName: "SillyTavernTray.exe")));
    }

    [Fact]
    public void NonPositiveOrOversizeAsset_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(size: 0)));
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(size: 301L * 1024 * 1024)));
    }

    [Fact]
    public void MalformedDigest_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse(ReleaseJson(hash: "nothex")));
    }
}
