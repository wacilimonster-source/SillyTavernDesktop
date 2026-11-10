using System.Diagnostics;

namespace SillyTavernTray;

internal static class ManagerUpdateInstaller
{
    internal const string HelperArgument = "--install-manager-update";
    internal const string SkipArgument = "--skip-manager-update-once";

    /// <summary>Launch the downloaded executable as an updater; it waits for this process to exit.</summary>
    public static bool TryLaunch(bool restart, bool autostart)
    {
        var pending = PendingManagerUpdateStore.Read();
        var target = Environment.ProcessPath;
        if (pending is null || pending.Failed || string.IsNullOrEmpty(target) ||
            Path.GetFileNameWithoutExtension(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(target).Equals(Path.GetFullPath(pending.TargetPath), StringComparison.OrdinalIgnoreCase) ||
            !ManagerVersion.TryParse(pending.Version, out var pendingVersion)) return false;
        if (ManagerVersion.Current >= pendingVersion)
        {
            try { PendingManagerUpdateStore.ClearIfSame(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        var stage = PendingManagerUpdateStore.StagePath(pending);
        if (!File.Exists(stage)) return false;
        try
        {
            if (!PendingManagerUpdateStore.HashFile(stage).Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("管理器更新文件校验失败。");
            var info = new ProcessStartInfo(stage) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = PendingManagerUpdateStore.DirectoryPath };
            info.ArgumentList.Add(HelperArgument);
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var current = Process.GetCurrentProcess();
            info.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(restart ? "1" : "0");
            info.ArgumentList.Add(autostart ? "1" : "0");
            using var helper = Process.Start(info);
            if (helper is null) throw new InvalidOperationException("无法启动管理器更新助手。");
            return true;
        }
        catch (Exception ex)
        {
            MarkFailed(pending, ex.Message);
            return false;
        }
    }

    public static int RunHelper(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[1], out var parentId) || !long.TryParse(args[2], out var parentStart) ||
            args[3] is not ("0" or "1") || args[4] is not ("0" or "1")) return 1;
        var restart = args[3] == "1";
        var autostart = args[4] == "1";
        var pending = PendingManagerUpdateStore.Read();
        if (pending is null) return 1;
        string? temporary = null;
        string? backup = null;
        var replaced = false;
        try
        {
            var ownPath = Path.GetFullPath(Environment.ProcessPath ?? "");
            var stage = Path.GetFullPath(PendingManagerUpdateStore.StagePath(pending));
            var target = Path.GetFullPath(pending.TargetPath);
            if (!ownPath.Equals(stage, StringComparison.OrdinalIgnoreCase) || stage.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(target).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("管理器更新路径无效。");
            if (!ManagerVersion.TryParse(pending.Version, out var expectedVersion) ||
                !Version.TryParse(FileVersionInfo.GetVersionInfo(stage).FileVersion, out var actualVersion) ||
                ManagerVersion.Normalize(actualVersion) != expectedVersion ||
                !PendingManagerUpdateStore.HashFile(stage).Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("管理器更新文件的版本或校验值不匹配。");
            WaitForOriginalProcess(parentId, parentStart);
            if (!File.Exists(target) || !PendingManagerUpdateStore.HashFile(target).Equals(pending.TargetSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("管理器程序在下载后发生变化，已取消自动替换。");
            var diskVersionText = FileVersionInfo.GetVersionInfo(target).FileVersion;
            if (Version.TryParse(diskVersionText, out var diskVersion) && ManagerVersion.Normalize(diskVersion) >= expectedVersion)
                throw new InvalidOperationException("磁盘上的管理器已经是该版本或更新版本，已取消自动替换。");
            var directory = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("管理器目录无效。");
            temporary = Path.Combine(directory, ".sillytavern-update-" + Guid.NewGuid().ToString("N") + ".tmp");
            backup = Path.Combine(directory, ".sillytavern-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(stage, temporary, overwrite: false);
            if (!PendingManagerUpdateStore.HashFile(temporary).Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("复制的管理器更新文件校验失败。");
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(temporary, target, backup, ignoreMetadataErrors: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
            }
            temporary = null;
            replaced = true;
            try { PendingManagerUpdateStore.ClearIfSame(pending); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The executable itself was installed successfully; a locked marker must not
                // trigger the rollback path below.
                new AppLog().Write("管理器更新", "清理待安装标记失败：" + cleanup.Message);
            }
            new AppLog().Write("管理器更新", $"管理器 {pending.Version} 已安装。");
            if (restart) StartTarget(target, autostart, skipOnce: false);
            try { if (backup is not null && File.Exists(backup)) File.Delete(backup); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { new AppLog().Write("管理器更新", "旧版备份暂时保留：" + backup); }
            backup = null;
            return 0;
        }
        catch (Exception ex)
        {
            var error = ex.Message;
            if (replaced && backup is not null && File.Exists(backup))
            {
                try { File.Replace(backup, pending.TargetPath, null, ignoreMetadataErrors: true); backup = null; }
                catch (Exception rollback) { error += "；恢复旧版失败，备份保留于 " + backup + "：" + rollback.Message; }
            }
            MarkFailed(pending, error);
            if (restart) try { StartTarget(pending.TargetPath, autostart, skipOnce: true); } catch { }
            return 1;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // If rollback failed, retain the old executable instead of deleting its backup.
        }
    }

    private static void WaitForOriginalProcess(int pid, long expectedStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks == expectedStartTicks && !process.WaitForExit(60000))
                throw new TimeoutException("等待管理器退出超时，更新未安装。");
        }
        catch (ArgumentException) { /* The original process has already exited. */ }
    }

    private static void StartTarget(string target, bool autostart, bool skipOnce)
    {
        var info = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (autostart) info.ArgumentList.Add("--autostart");
        if (skipOnce) info.ArgumentList.Add(SkipArgument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法重新启动管理器。");
    }

    private static void MarkFailed(PendingManagerUpdate pending, string error)
    {
        try
        {
            var current = PendingManagerUpdateStore.Read();
            if (current is null) PendingManagerUpdateStore.Write(pending with { Failed = true, Error = error });
            else if (current.FileName == pending.FileName && current.Sha256.Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                PendingManagerUpdateStore.Write(current with { Failed = true, Error = error });
            new AppLog().Write("管理器更新", "自动安装失败：" + error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
