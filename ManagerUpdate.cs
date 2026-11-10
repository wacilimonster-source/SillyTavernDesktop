using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SillyTavernTray;

internal sealed record ManagerRelease(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256);
internal sealed record PendingManagerUpdate(string Version, string FileName, string Sha256, string TargetPath, string TargetSha256,
    bool Failed = false, string? Error = null);

internal static class ManagerVersion
{
    public static Version Current => Normalize(typeof(ManagerVersion).Assembly.GetName().Version ?? new Version(1, 0, 0));
    public static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(value.Build, 0));
    public static bool TryParse(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (!Regex.IsMatch(text, @"^v?\d{1,5}\.\d{1,5}\.\d{1,5}$", RegexOptions.CultureInvariant)) return false;
        if (!Version.TryParse(text.TrimStart('v'), out var parsed) || parsed is null) return false;
        version = Normalize(parsed);
        return true;
    }
}

internal static class PendingManagerUpdateStore
{
    public static string DirectoryPath => Path.Combine(AppSettings.DataDirectory, "manager-updates");
    private static string PendingPath => Path.Combine(DirectoryPath, "pending.json");

    public static PendingManagerUpdate? Read()
    {
        try
        {
            if (!File.Exists(PendingPath)) return null;
            var pending = JsonSerializer.Deserialize<PendingManagerUpdate>(File.ReadAllText(PendingPath));
            if (pending is null || !ManagerVersion.TryParse(pending.Version, out _) ||
                !Regex.IsMatch(pending.FileName, @"^SillyTavernTray-v\d{1,5}\.\d{1,5}\.\d{1,5}\.exe$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(pending.Sha256, "^[0-9a-fA-F]{64}$") || !Regex.IsMatch(pending.TargetSha256, "^[0-9a-fA-F]{64}$") ||
                !Path.IsPathFullyQualified(pending.TargetPath)) return null;
            return pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    public static string StagePath(PendingManagerUpdate pending) => Path.Combine(DirectoryPath, pending.FileName);

    public static void Write(PendingManagerUpdate pending)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = Path.Combine(DirectoryPath, "pending-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PendingPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ClearIfSame(PendingManagerUpdate pending)
    {
        var current = Read();
        if (current is not null && current.FileName == pending.FileName && current.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
            File.Delete(PendingPath);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void CleanupStaleFiles()
    {
        if (!Directory.Exists(DirectoryPath)) return;
        var keep = Read()?.FileName;
        try
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "SillyTavernTray-v*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name.Equals(keep, StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(name, @"^SillyTavernTray-v\d{1,5}\.\d{1,5}\.\d{1,5}\.exe$", RegexOptions.CultureInvariant)) continue;
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // Interrupted downloads/atomic writes leave only harmless temp files; no
            // download can be active while the single manager instance is starting.
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class ManagerUpdateService : IDisposable
{
    internal const string AssetName = "SillyTavernTray-win-x64.exe";
    internal const string LatestUrl = "https://api.github.com/repos/wacilimonster-source/SillyTavernDesktop/releases/latest";
    private const long MaxAssetBytes = 300L * 1024 * 1024;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly string targetPath;
    private readonly Version currentVersion;
    private readonly AppLog log;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    public event Action<string>? StatusChanged;

    public ManagerUpdateService(AppLog log, HttpClient? client = null, string? targetPath = null, Version? currentVersion = null)
    {
        this.log = log;
        this.targetPath = Path.GetFullPath(targetPath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定管理器程序路径。"));
        this.currentVersion = ManagerVersion.Normalize(currentVersion ?? ManagerVersion.Current);
        if (client is null)
        {
#pragma warning disable SYSLIB0014 // The Windows-user proxy API includes manual proxy and PAC settings.
            var handler = new HttpClientHandler { Proxy = WebRequest.GetSystemWebProxy(), UseProxy = true };
#pragma warning restore SYSLIB0014
            this.client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
            ownsClient = true;
        }
        else this.client = client;
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("SillyTavernDesktop/" + this.currentVersion);
        this.client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<string> CheckAsync(bool manual, CancellationToken cancellation = default)
    {
        if (Path.GetFileNameWithoutExtension(targetPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return "开发模式不自动替换 dotnet，请使用发布的管理器程序。";
        if (!await Gate.WaitAsync(0, cancellation)) return "管理器更新检查正在进行。";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, cancellation);
        try
        {
            StatusChanged?.Invoke("正在检查管理器更新…");
            using var apiTimeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            apiTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var apiResponse = await client.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead, apiTimeout.Token);
            if (apiResponse.StatusCode == HttpStatusCode.NotFound) return "暂时没有管理器发行版本。";
            apiResponse.EnsureSuccessStatusCode();
            if (apiResponse.Content.Headers.ContentLength is > 1024 * 1024) throw new InvalidDataException("管理器更新信息过大。");
            await using var apiStream = await apiResponse.Content.ReadAsStreamAsync(apiTimeout.Token);
            using var apiContent = new MemoryStream();
            var apiBuffer = new byte[16384];
            int apiCount;
            while ((apiCount = await apiStream.ReadAsync(apiBuffer, apiTimeout.Token)) > 0)
            {
                if (apiContent.Length + apiCount > 1024 * 1024) throw new InvalidDataException("管理器更新信息过大。");
                await apiContent.WriteAsync(apiBuffer.AsMemory(0, apiCount), apiTimeout.Token);
            }
            using var document = JsonDocument.Parse(apiContent.ToArray());
            var release = ParseRelease(document.RootElement);
            if (release.Version <= currentVersion) return "管理器已是最新版本（" + currentVersion + "）。";
            var currentPending = PendingManagerUpdateStore.Read();
            if (currentPending is not null && currentPending.Version == release.Tag &&
                currentPending.TargetPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase) &&
                currentPending.Sha256.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(PendingManagerUpdateStore.StagePath(currentPending)) &&
                PendingManagerUpdateStore.HashFile(PendingManagerUpdateStore.StagePath(currentPending)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                if (manual && currentPending.Failed)
                {
                    if (!PendingManagerUpdateStore.HashFile(targetPath).Equals(currentPending.TargetSha256, StringComparison.OrdinalIgnoreCase))
                        return "管理器程序已发生变化，请手动检查后从 GitHub Release 安装。";
                    PendingManagerUpdateStore.Write(currentPending with { Failed = false, Error = null });
                }
                return currentPending.Failed && !manual ? "管理器更新等待处理，请手动检查管理器更新。" : $"管理器 {release.Tag} 已下载，将在退出后安装。";
            }
            StatusChanged?.Invoke("正在下载管理器 " + release.Tag + "…");
            await DownloadAsync(release, linked.Token);
            var result = $"管理器 {release.Tag} 已下载，将在退出后安装。";
            log.Write("管理器更新", result);
            StatusChanged?.Invoke(result);
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return "管理器更新检查已取消。"; }
        catch (OperationCanceledException) { throw new TimeoutException("读取管理器更新信息超时。"); }
        finally { Gate.Release(); }
    }

    internal static ManagerRelease ParseRelease(JsonElement root)
    {
        if ((root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) ||
            (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())) throw new InvalidDataException("管理器发行版本尚未正式发布。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!ManagerVersion.TryParse(tag, out var version) || !tag.StartsWith('v')) throw new InvalidDataException("管理器版本号无效。");
        var expectedPath = "/wacilimonster-source/SillyTavernDesktop/releases/download/" + tag + "/" + AssetName;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            var urlText = asset.GetProperty("browser_download_url").GetString();
            var digest = asset.TryGetProperty("digest", out var hashValue) ? hashValue.GetString() ?? "" : "";
            var size = asset.GetProperty("size").GetInt64();
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var url) || url.Scheme != "https" ||
                !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath != expectedPath ||
                !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
                size <= 0 || size > MaxAssetBytes || !Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$"))
                throw new InvalidDataException("管理器发布包地址、大小或 SHA-256 校验信息无效。");
            return new ManagerRelease(version, tag, url, size, digest[7..].ToLowerInvariant());
        }
        throw new InvalidDataException("管理器发行版本缺少 Windows x64 安装包。");
    }

    private async Task DownloadAsync(ManagerRelease release, CancellationToken cancellation)
    {
        Directory.CreateDirectory(PendingManagerUpdateStore.DirectoryPath);
        var temporary = Path.Combine(PendingManagerUpdateStore.DirectoryPath, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != release.Size)
                throw new InvalidDataException("管理器发布包大小不匹配。");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count;
                    if (total > release.Size || total > MaxAssetBytes) throw new InvalidDataException("管理器发布包超过预期大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("管理器发布包校验失败。");
            }
            var fileVersion = FileVersionInfo.GetVersionInfo(temporary).FileVersion;
            if (!Version.TryParse(fileVersion, out var parsed) || ManagerVersion.Normalize(parsed) != release.Version)
                throw new InvalidDataException("管理器发布包的程序版本与发行版本不一致。");
            var targetVersionText = FileVersionInfo.GetVersionInfo(targetPath).FileVersion;
            if (Version.TryParse(targetVersionText, out var targetVersion) && ManagerVersion.Normalize(targetVersion) >= release.Version)
                throw new InvalidOperationException("磁盘上的管理器已经是该版本或更新版本，已取消自动替换。");
            var fileName = "SillyTavernTray-" + release.Tag + ".exe";
            var destination = Path.Combine(PendingManagerUpdateStore.DirectoryPath, fileName);
            File.Move(temporary, destination, overwrite: true);
            PendingManagerUpdateStore.Write(new PendingManagerUpdate(release.Tag, fileName, release.Sha256, targetPath, PendingManagerUpdateStore.HashFile(targetPath)));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<bool> PrepareForExitAsync()
    {
        shutdown.Cancel();
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(5))) return false;
        Gate.Release();
        return true;
    }

    public void Dispose()
    {
        shutdown.Cancel();
        if (ownsClient) client.Dispose();
        // The static gate may still be released by a canceled asynchronous download.
    }
}
