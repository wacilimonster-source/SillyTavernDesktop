using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;

namespace SillyTavernTray;

// Status card rendering: UpdateDisplay is the single place that maps app state onto
// controls, plus the elapsed/compact helpers it needs.
internal sealed partial class MainForm
{
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 1000 };
    private bool compactMode;
    private (int ProcessId, string Name)? occupierInfo;
    private DateTime occupierQueriedUtc = DateTime.MinValue;
    private IReadOnlyList<ViewAction>? lastActionSet;
    private StatusTone? lastActionTone;

    private (int ProcessId, string Name)? QueryOccupier(int port)
    {
        if (DateTime.UtcNow - occupierQueriedUtc > TimeSpan.FromSeconds(5))
        {
            occupierQueriedUtc = DateTime.UtcNow;
            occupierInfo = ServerController.FindPortOwner(port);
        }
        return occupierInfo;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        if (duration < TimeSpan.FromMinutes(1)) return "刚刚启动";
        if (duration < TimeSpan.FromHours(1)) return $"{(int)duration.TotalMinutes} 分钟";
        if (duration < TimeSpan.FromDays(1)) return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        return $"{(int)duration.TotalDays} 天 {(int)duration.Hours} 小时";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return seconds < 60 ? $"已等待 {seconds} 秒" : $"已等待 {seconds / 60} 分 {seconds % 60} 秒";
    }

    private void UpdateDisplay()
    {
        if (IsDisposed) return;
        var p = Theme.Current;
        var configuration = CurrentConfiguration();
        var config = server.Config;
        var securityWord = config is null ? "" : config.BasicAuth || config.UserAccounts ? "认证开" : config.Whitelist ? "认证未开" : "无访问限制";
        string metadata;
        if (config is not null)
        {
            metadata = $"ST {config.Version} · 端口 {config.Port} · {branch} · {securityWord}";
            if (server.State == ServerState.Running && runningSinceUtc is { } since)
                metadata += " · 已运行 " + FormatDuration(DateTime.UtcNow - since);
            if (server.State == ServerState.Failed)
            {
                if (server.LastExitCode is { } code) metadata += $" · 退出码 {code}";
                if (lastRunDuration is { } duration) metadata += " · 已运行 " + FormatDuration(duration);
            }
            if (server.State == ServerState.Occupied && QueryOccupier(config.Port) is { } owner)
                metadata += $" · 占用进程 {owner.Name} (PID {owner.ProcessId})";
        }
        else metadata = $"管理器 {ManagerVersion.Current} · 等待读取 ST 配置";

        // The occupied note guides the user instead of repeating the meta line (v2 §4.3).
        var stateDetail = server.State == ServerState.Occupied
            ? "若为上次异常退出留下的服务，可结束该进程后点「重新检测」。管理器不会结束或接管其他进程。"
            : server.Detail;
        var canCancel = maintenanceCancel is { IsCancellationRequested: false } && currentStepIndex == 0;
        var state = AppStateFactory.Compute(server.State, server.HasOwnedProcess, busyOperation, configuration, stateDetail, metadata,
            currentSteps, canCancel);

        statusCard.FillColor = p.ToneSurface(state.Tone);
        statusCard.BorderColor = state.Tone == StatusTone.Stopped
            ? p.Border
            : Draw.Blend(p.ToneColor(state.Tone), p.Border, 0.72);
        statusCard.Invalidate();
        statusDot.DotColor = p.ToneColor(state.Tone);
        statusLabel.Text = state.Title;
        metaLabel.Text = state.Note is null ? state.Metadata : state.Metadata + "  ·  " + state.Note;
        metaLabel.Tone = state.Note is null ? LabelTone.Secondary
            : state.Tone == StatusTone.Error ? LabelTone.Danger : LabelTone.Warning;

        // Watchdog badge: visible for 30 minutes after an automatic restart or until the next manual action.
        var showBadge = !state.OnboardingVisible && WatchdogBadgeActive &&
                        server.State is ServerState.Running or ServerState.Failed && busyOperation == BusyOperation.None;
        watchdogBadge.Visible = showBadge;
        if (showBadge)
        {
            watchdogBadge.Text = "· 看门狗 " + lastWatchdogRestartAt!.Value.ToString("HH:mm") + " 自动重启";
            var paused = watchdogPausedUntilUtc > DateTime.UtcNow;
            toolTip.SetToolTip(watchdogBadge,
                $"本次会话第 {watchdogRestartCount} 次自动重启；最近 {lastWatchdogRestartAt.Value:HH:mm:ss}。" +
                (paused ? "连续超限，自动重启已暂停 10 分钟。" : ""));
        }

        // Status card height follows the busy extras: progress line, step bar and cancel row.
        var showSteps = state.ShowProgress && state.Steps is { Count: > 0 };
        var showCancel = state.ShowProgress && busyOperation is BusyOperation.Updating or BusyOperation.Repairing;
        statusGrid.RowStyles[2].Height = state.ShowProgress ? 0 : S(34);
        statusGrid.RowStyles[3].Height = state.ShowProgress ? S(26) : 0;
        statusGrid.RowStyles[4].Height = showSteps ? S(30) : 0;
        statusGrid.RowStyles[5].Height = showCancel ? S(26) : 0;
        layout.RowStyles[0].Height = S(124) - (state.ShowProgress ? S(34) : 0)
            + (state.ShowProgress ? S(26) : 0) + (showSteps ? S(30) : 0) + (showCancel ? S(26) : 0);
        progressFlow.Visible = state.ShowProgress;
        statusActions.Visible = !state.ShowProgress;
        progressLabel.Text = state.ProgressLabel.TrimEnd('…');
        UpdateBusyElapsed();
        if (showSteps && state.Steps is { } steps) stepProgress.SetSteps(steps);
        stepProgress.Visible = showSteps;
        cancelFlow.Visible = showCancel;
        if (showCancel)
        {
            cancelMaintenanceButton.Enabled = canCancel;
            cancelHintLabel.Text = canCancel ? "预检阶段可取消" : "此阶段无法安全取消（npm 半安装会损坏 node_modules）";
            toolTip.SetToolTip(cancelMaintenanceButton, canCancel ? "取消本次维护" : "此阶段无法安全取消（npm 半安装会损坏 node_modules）");
        }

        primaryKind = state.PrimaryKind;
        primaryButton.Backdrop = p.ToneSurface(state.Tone);
        primaryButton.Visible = !state.ShowProgress && state.PrimaryKind != PrimaryActionKind.None;
        if (primaryButton.Visible)
        {
            primaryButton.Text = state.PrimaryLabel;
            primaryButton.IconGlyph = state.PrimaryIcon;
            primaryButton.AccessibleName = state.PrimaryLabel;
            primaryButton.Size = primaryButton.GetPreferredSize(Size.Empty);
        }

        RebuildStatusActions(state, p);

        var addressEnabled = state.AddressEnabled;
        addressPicker.Enabled = addressEnabled;
        copyAddressButton.Enabled = addressEnabled;
        UpdateAddressText();
        if (addressEnabled) PopulateAddresses();

        var maintenanceEnabled = busyOperation == BusyOperation.None && configuration is ConfigurationState.Configured or ConfigurationState.Pending;
        checkUpdateButton.Enabled = updateButton.Enabled = repairButton.Enabled = maintenanceEnabled;

        var showOnboarding = state.OnboardingVisible;
        onboarding.Visible = showOnboarding;
        logSection.Visible = !showOnboarding;
        if (logRow is not null && onboardingRow is not null)
        {
            if (showOnboarding)
            {
                logRow.SizeType = SizeType.Absolute;
                logRow.Height = 0;
                onboardingRow.SizeType = SizeType.Percent;
                onboardingRow.Height = 100;
            }
            else
            {
                onboardingRow.SizeType = SizeType.Absolute;
                onboardingRow.Height = 0;
                logRow.SizeType = SizeType.Percent;
                logRow.Height = 100;
            }
            layout.PerformLayout();
        }

        var pending = ReadPendingUpdate();
        var showBanner = pending is { Failed: false } && !bannerDismissed && busyOperation == BusyOperation.None;
        if (bannerRow is not null)
        {
            bannerRow.Height = showBanner ? S(60) : 0;
            banner.Visible = showBanner;
            if (showBanner)
            {
                bannerLabel.Text = $"管理器 {pending!.Version} 已就绪，退出时自动安装";
                bannerAction.Enabled = busyOperation == BusyOperation.None;
            }
            layout.PerformLayout();
        }

        var running = server.HasOwnedProcess;
        trayStartStop.Text = running ? "停止服务" : "启动服务";
        var startStopGlyph = running ? Icons.Stop : Icons.Play;
        if (trayGlyphs.TryGetValue(trayStartStop, out var currentGlyph) && currentGlyph != startStopGlyph)
        {
            trayGlyphs[trayStartStop] = startStopGlyph;
            var oldImage = trayStartStop.Image;
            trayStartStop.Image = Icons.ToBitmap(startStopGlyph, S(16), p.TextSecondary);
            oldImage?.Dispose();
        }
        trayStartStop.Enabled = busyOperation == BusyOperation.None;
        trayRestart.Enabled = busyOperation == BusyOperation.None && running;
        trayOpenServer.Enabled = running;
        trayUpdate.Enabled = busyOperation == BusyOperation.None;
        trayManagerUpdate.Enabled = busyOperation == BusyOperation.None;
        trayRepair.Enabled = busyOperation == BusyOperation.None && configuration is ConfigurationState.Configured or ConfigurationState.Pending;
        traySettings.Enabled = busyOperation == BusyOperation.None;
        trayExit.Enabled = busyOperation == BusyOperation.None;

        trayHeader.Status = state.Title + (config?.Port is int port ? $" · 端口 {port}" : "");
        trayHeader.Subtext = config is null ? $"管理器 {ManagerVersion.Current}" : $"ST {config.Version} · 管理器 {ManagerVersion.Current}";
        trayHeader.DotColor = p.ToneColor(state.Tone);
        trayHeader.Invalidate();
        tray.Text = ("SillyTavern：" + server.Detail)[..Math.Min(63, ("SillyTavern：" + server.Detail).Length)];
        SetTrayIcon(TrayStatus.ColorFor(state.Tone));
        pauseButton.Glyph = pausedScroll ? Icons.Play : Icons.Pause;
        UpdateCompactMode();
        PositionBackToBottom();
    }

    private void RebuildStatusActions(AppViewState state, Palette p)
    {
        // Rebuilding disposes and recreates every button on each UpdateDisplay; skip when the
        // action set and its tone are unchanged (records compare by value).
        if (lastActionTone == state.Tone && lastActionSet is not null && lastActionSet.SequenceEqual(state.Actions)) return;
        lastActionTone = state.Tone;
        lastActionSet = state.Actions;
        foreach (Control control in statusActions.Controls) control.Dispose();
        statusActions.Controls.Clear();
        foreach (var action in state.Actions)
        {
            var button = new ThemedButton
            {
                Text = action.Label,
                IconGlyph = action.Icon,
                Variant = ButtonVariant.Quiet,
                PreferredHeight = 28,
                DangerHover = action.Danger,
                Enabled = action.Enabled,
                Backdrop = p.ToneSurface(state.Tone),
                Margin = new Padding(0, 0, S(2), 0)
            };
            button.Click += async (_, _) => await HandleActionAsync(action.Kind);
            button.AccessibleName = action.Label;
            statusActions.Controls.Add(button);
        }
    }

    private void UpdateBusyElapsed()
    {
        if (busyOperation == BusyOperation.None || !progressFlow.Visible || hiddenToTray)
        {
            elapsedLabel.Text = "";
            return;
        }
        var elapsed = DateTime.UtcNow - busySinceUtc;
        elapsedLabel.Text = elapsed < TimeSpan.FromSeconds(1) ? "" : "· " + FormatElapsed(elapsed);
    }

    private void UpdateCompactMode()
    {
        var compact = Width < S(680);
        if (compact == compactMode) return;
        compactMode = compact;
        logSearch.Visible = !compact;
        searchToggleButton.Visible = compact;
        checkUpdateButton.Text = compact ? "检查" : "检查更新";
        updateButton.Text = compact ? "更新" : "更新 ST";
        repairButton.Text = compact ? "修复" : "依赖修复";
    }
}
