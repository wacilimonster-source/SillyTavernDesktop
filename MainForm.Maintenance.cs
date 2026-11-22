using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;
using SillyTavernTray.Ui.Dialogs;

namespace SillyTavernTray;

// Maintenance flows: ST update/repair with the step bar, and the manager's own
// update check/download orchestration.
internal sealed partial class MainForm
{
    private PendingManagerUpdate? pendingUpdateCache;
    private bool pendingUpdateCacheValid;
    private ManagerUpdateService? managerUpdates;
    private readonly System.Windows.Forms.Timer managerUpdateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly CancellationTokenSource managerUpdateLifetime = new();
    private CancellationTokenSource autoUpdateCheck = new();
    private string? lastUpdateNotification;
    private (string Kind, DateTime Time)? lastMaintenance;

    private PendingManagerUpdate? ReadPendingUpdate()
    {
        if (!pendingUpdateCacheValid)
        {
            pendingUpdateCache = PendingManagerUpdateStore.Read();
            pendingUpdateCacheValid = true;
        }
        return pendingUpdateCache;
    }

    private void OnPendingUpdateStoreChanged()
    {
        // The store changed on a background task (download completion); refresh the cached record.
        pendingUpdateCacheValid = false;
        TryUi(UpdateDisplay);
    }

    private async Task CheckUpdatesAsync() => await RunExclusiveAsync(BusyOperation.CheckingUpdates, async () =>
    {
        var result = await maintenance.CheckUpdatesAsync();
        ShowToast(result, ToastKind.Info);
    });

    private async Task RefreshEnvironmentAsync() => await RunExclusiveAsync(BusyOperation.ReadingConfig, async () =>
    {
        var branchTask = maintenance.GetBranchAsync();
        await server.RefreshConfigAsync();
        branch = await branchTask;
    });

    private async Task CheckManagerUpdateAsync(bool manual)
    {
        if (reallyClosing || (!manual && !settings.AutoUpdateManager)) return;
        try
        {
            if (managerUpdates is null)
            {
                managerUpdates = new ManagerUpdateService(log);
                managerUpdates.StatusChanged += message => TryUi(() => trayManagerUpdate.Text = message);
            }
            using var linked = manual ? CancellationTokenSource.CreateLinkedTokenSource(managerUpdateLifetime.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(managerUpdateLifetime.Token, autoUpdateCheck.Token);
            var result = await managerUpdates.CheckAsync(manual, linked.Token);
            if (IsDisposed || reallyClosing) return;
            UpdateDisplay();
            if (manual) ShowToast(result, ToastKind.Info);
            else if (result.Contains("已下载", StringComparison.Ordinal) && result != lastUpdateNotification)
            {
                lastUpdateNotification = result;
                Notify("管理器更新", result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("管理器更新", "检查失败：" + ex.Message);
            if (manual && !IsDisposed) ShowToast("管理器更新检查失败：" + ex.Message, ToastKind.Warning);
        }
        finally { if (!IsDisposed) trayManagerUpdate.Text = "检查管理器更新"; }
    }

    private async Task UpdateAsync()
    {
        if (!ConfirmDialog.AskConfirm(this, "更新 SillyTavern",
            "更新将按官方命令依次执行：\n" +
            "1. 预检 Git 状态与上游\n" +
            "2. 停止由管理器启动的服务\n" +
            "3. git pull --rebase --autostash\n" +
            "4. npm install（生产依赖）\n" +
            "5. 重新启动服务\n\n" +
            "正在进行的聊天可能中断；本地未提交修改会阻止更新。", "开始更新")) return;
        ClearWatchdogBadge();
        currentSteps =
        [
            new("预检", StepStatus.Pending), new("停止服务", StepStatus.Pending), new("拉取更新", StepStatus.Pending),
            new("安装依赖", StepStatus.Pending), new("重启服务", StepStatus.Pending)
        ];
        await RunExclusiveAsync(BusyOperation.Updating, async () =>
        {
            await maintenance.UpdateAsync(AskStopDecisionAsync, maintenanceCancel?.Token ?? default);
            branch = await maintenance.GetBranchAsync();
        });
    }

    private async Task RepairAsync()
    {
        if (!ConfirmDialog.AskConfirm(this, "安装依赖",
            "依赖修复将按官方命令依次执行：\n" +
            "1. 预检 Node.js 与 npm\n" +
            "2. 停止由管理器启动的服务\n" +
            "3. npm install（生产依赖）\n" +
            "4. 重新启动服务\n\n" +
            "正在进行的聊天可能中断。", "开始修复")) return;
        ClearWatchdogBadge();
        currentSteps =
        [
            new("预检", StepStatus.Pending), new("停止服务", StepStatus.Pending),
            new("安装依赖", StepStatus.Pending), new("重启服务", StepStatus.Pending)
        ];
        await RunExclusiveAsync(BusyOperation.Repairing, () => maintenance.RepairAsync(AskStopDecisionAsync, maintenanceCancel?.Token ?? default));
    }

    private Task<StopDecision> AskStopDecisionAsync()
    {
        var waited = server.StopWaitedSeconds;
        var choice = ConfirmDialog.AskStop(this,
            "SillyTavern 没有在限定时间内退出。\n强制结束可能中断尚未完成的操作。",
            waited > 0 ? $"服务尚未退出（已等待 {waited} 秒）" : null);
        return Task.FromResult(choice switch
        {
            StopChoice.Wait => StopDecision.Wait,
            StopChoice.Force => StopDecision.Force,
            _ => StopDecision.Cancel
        });
    }

    private void CancelMaintenance()
    {
        if (maintenanceCancel is not { IsCancellationRequested: false }) return;
        if (currentStepIndex != 0)
        {
            ShowToast("此阶段无法安全取消（npm 半安装会损坏 node_modules）。", ToastKind.Warning);
            return;
        }
        maintenanceCancel.Cancel();
        log.Write("管理器", busyOperation == BusyOperation.Updating ? "用户取消了本次更新（预检阶段）。" : "用户取消了本次依赖修复（预检阶段）。");
    }

    private void OnStepChanged(int index, StepStatus status)
    {
        if (index < 0 || index >= currentSteps.Count) return;
        currentSteps[index] = currentSteps[index] with { Status = status };
        if (status == StepStatus.Active) currentStepIndex = index;
        UpdateDisplay();
    }
}
