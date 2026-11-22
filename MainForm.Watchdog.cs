using SillyTavernTray.Ui;

namespace SillyTavernTray;

// Watchdog: heartbeat-based stall detection plus the crash-restart flow, including
// the restart limiter that keeps a crash loop from becoming a restart storm.
internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer watchdogTimer = new() { Interval = 10_000 };
    private readonly RestartLimiter watchdogLimiter = new();
    private int watchdogRestartCount;
    private DateTime? lastWatchdogRestartAt;
    private TimeSpan? lastRunDuration;
    private DateTime? runningSinceUtc;
    private DateTime watchdogPausedUntilUtc = DateTime.MinValue;
    private ServerState previousServerState = ServerState.Stopped;
    private bool watchdogRestarting;
    private bool heartbeatUnavailableLogged;

    private void OnServerChanged()
    {
        // Capture the state on the background thread: queued UI callbacks must not
        // observe a newer transition and skip a crash notification.
        var observed = server.State;
        var detail = server.Detail;
        TryUi(() => HandleServerState(observed, detail));
    }

    private void HandleServerState(ServerState current, string detail)
    {
        var previous = previousServerState;
        previousServerState = current;
        if (current == ServerState.Running) runningSinceUtc = DateTime.UtcNow;
        if (current == ServerState.Failed)
        {
            if (previous is ServerState.Running or ServerState.Starting && runningSinceUtc is { } since)
                lastRunDuration = DateTime.UtcNow - since;
            if (watchdogRestarting) { /* the auto-restart flow reports its own outcome */ }
            else if (previous is ServerState.Running or ServerState.Starting && settings.AutoRestartServer && !reallyClosing && busyOperation == BusyOperation.None)
                BeginWatchdogRestart("检测到服务异常退出", delay: true);
            else Notify("SillyTavern 已退出", detail);
        }
        UpdateDisplay();
    }

    private void CheckWatchdog()
    {
        if (reallyClosing || !settings.AutoRestartServer || watchdogRestarting || busyOperation != BusyOperation.None) return;
        if (server.State != ServerState.Running || !server.HasOwnedProcess) return;
        if (server.Config is not { SupportsHeartbeat: true }) return;
        var now = DateTime.UtcNow;
        if (now < watchdogPausedUntilUtc) return;
        var heartbeat = server.HeartbeatPath;
        if (string.IsNullOrEmpty(heartbeat)) return;
        DateTime? lastWrite = null;
        try { if (File.Exists(heartbeat)) lastWrite = File.GetLastWriteTimeUtc(heartbeat); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var observation = WatchdogEvaluator.Evaluate(now, server.State, runningSinceUtc, lastWrite);
        if (observation == WatchdogObservation.Healthy) { heartbeatUnavailableLogged = false; return; }
        if (observation == WatchdogObservation.Missing)
        {
            // Older SillyTavern versions (and forks) never write a heartbeat. Missing must not
            // trigger restarts; process crashes are still handled by the failed-state path.
            if (!heartbeatUnavailableLogged)
            {
                heartbeatUnavailableLogged = true;
                log.Write("管理器", "未检测到心跳文件，已跳用心跳自动重启（旧版或未启用心跳）。");
            }
            return;
        }
        if (observation != WatchdogObservation.Stale) return;
        var age = $"{Math.Max(0, (int)(now - lastWrite!.Value).TotalSeconds)} 秒未更新";
        BeginWatchdogRestart($"检测到服务无响应（{age}）", delay: false);
    }

    private bool BeginWatchdogRestart(string reason, bool delay)
    {
        if (reallyClosing || !settings.AutoRestartServer || watchdogRestarting || busyOperation != BusyOperation.None) return false;
        var now = DateTime.UtcNow;
        if (now < watchdogPausedUntilUtc) return false;
        if (!watchdogLimiter.TryAcquire(now))
        {
            watchdogPausedUntilUtc = now + TimeSpan.FromMinutes(10);
            log.Write("管理器", "看门狗：短时间内重启次数过多，已暂停自动重启 10 分钟。请检查日志或手动启动服务。");
            Notify("自动重启已暂停", "10 分钟内重启次数过多，请检查日志。");
            return false;
        }
        watchdogRestarting = true;
        log.Write("管理器", "看门狗：" + reason + "，准备自动重启服务。");
        Notify("SillyTavern 正在自动重启", reason);
        _ = AutoRestartAsync(delay);
        return true;
    }

    private async Task AutoRestartAsync(bool delay)
    {
        try
        {
            if (delay) await Task.Delay(TimeSpan.FromSeconds(3));
            if (reallyClosing || !settings.AutoRestartServer || busyOperation != BusyOperation.None) return;
            if (server.HasOwnedProcess)
            {
                var stopped = false;
                await RunExclusiveAsync(BusyOperation.Stopping,
                    async () => { stopped = await server.StopAsync(() => Task.FromResult(StopDecision.Force)); },
                    showErrors: false);
                if (!stopped && server.HasOwnedProcess)
                {
                    log.Write("管理器", "看门狗：无法停止卡住的服务，跳过本次自动重启。");
                    Notify("自动重启失败", "无法停止当前服务，请手动处理。");
                    return;
                }
            }
            await RunExclusiveAsync(BusyOperation.Starting, async () =>
            {
                var branchTask = maintenance.GetBranchAsync();
                await server.StartAsync();
                branch = await branchTask;
            }, showErrors: false);
            if (server.State is ServerState.Running or ServerState.Starting)
            {
                watchdogRestartCount++;
                lastWatchdogRestartAt = DateTime.Now;
                log.Write("管理器", "看门狗：服务已自动重启。");
                Notify("SillyTavern 已自动重启", "看门狗检测到异常并恢复了服务。");
            }
            else
            {
                log.Write("管理器", "看门狗：自动重启未成功，" + server.Detail);
                Notify("自动重启失败", server.Detail);
            }
        }
        catch (Exception ex)
        {
            log.Write("管理器", "看门狗：自动重启失败：" + ex.Message);
            Notify("自动重启失败", ex.Message);
        }
        finally
        {
            watchdogRestarting = false;
            if (!IsDisposed) UpdateDisplay();
        }
    }

    private bool WatchdogBadgeActive => lastWatchdogRestartAt is { } at && DateTime.Now - at < TimeSpan.FromMinutes(30);

    private void ClearWatchdogBadge()
    {
        if (watchdogRestartCount == 0 && lastWatchdogRestartAt is null) return;
        watchdogRestartCount = 0;
        lastWatchdogRestartAt = null;
    }
}
