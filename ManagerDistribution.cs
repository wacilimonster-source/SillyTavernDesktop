namespace SillyTavernTray;

/// <summary>
/// Single source of truth for where this manager distributes itself: the GitHub
/// repository it checks for releases, the asset it downloads, and the project
/// URLs shown in the UI. Every other reference to the repository derives from
/// here so a fork or rename only changes one file.
/// </summary>
internal static class ManagerDistribution
{
    public const string RepoOwner = "wacilimonster-source";
    public const string RepoName = "SillyTavernDesktop";
    /// <summary>Release asset the self-update flow downloads and installs.</summary>
    public const string AssetName = "SillyTavernTray-win-x64.exe";

    public static string LatestApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    /// <summary>Absolute path a release download URL must carry to be trusted.</summary>
    public static string DownloadPath(string tag) => $"/{RepoOwner}/{RepoName}/releases/download/{tag}/{AssetName}";

    public static string ProjectUrl => $"https://github.com/{RepoOwner}/{RepoName}";
}
