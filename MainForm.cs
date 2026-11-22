using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms.Automation;
using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;
using SillyTavernTray.Ui.Dialogs;

namespace SillyTavernTray;

// Core window: services, the constructor that assembles the partial files' UI,
// the exclusive-busy orchestration every action funnels through, and the
// window/tray lifecycle (show/hide/exit/dispose).
// UI construction lives in MainForm.Layout.cs, the status card in
// MainForm.StateCard.cs, maintenance flows in MainForm.Maintenance.cs, the
// watchdog in MainForm.Watchdog.cs, the log panel in MainForm.LogPanel.cs and
// user actions in MainForm.Actions.cs.
internal sealed partial class MainForm : ThemedForm
{
    private readonly bool autoStarted;
    private readonly EventWaitHandle showSignal;
    private readonly AppSettings settings;
    private readonly AppLog log = new();
    private readonly ServerController server;
    private readonly MaintenanceService maintenance;
    private readonly ToastHost toast = new();

    // Shared orchestration state: written by RunExclusiveAsync and the watchdog,
    // read by UpdateDisplay and the action handlers in the other partial files.
    private BusyOperation busyOperation;
    private DateTime busySinceUtc = DateTime.MinValue;
    private List<StepState> currentSteps = [];
    private int currentStepIndex;
    private CancellationTokenSource? maintenanceCancel;
    private string branch = "未知";
    private PrimaryActionKind primaryKind;
    private bool configReadFailed;
    private bool reallyClosing, bannerDismissed;
    private volatile bool stopSignalThread;
    private bool hiddenToTray;
    private bool firstTrayHide = true;

    static MainForm() { Theme.Initialize(); }

    public MainForm(bool autoStarted, EventWaitHandle showSignal)
    {
        this.autoStarted = autoStarted;
        this.showSignal = showSignal;
        Theme.Initialize();
        settings = AppSettings.Load();
        Theme.ApplyAppearance(settings.Appearance);
        if (AppSettings.RecoveryNotice is { } recovery) log.Write("管理器", recovery);
        server = new ServerController(settings, log);
        maintenance = new MaintenanceService(settings, log, server);

        Text = "SillyTavern 管理器";
        Font = Typography.Body;
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        _ = Handle;
        var scale = DeviceDpi / 96f;
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        var maxWidth = Math.Max(S(400), area.Width - S(24));
        var maxHeight = Math.Max(S(320), area.Height - S(24));
        MinimumSize = new Size(
            Math.Min((int)Math.Round(600 * scale), maxWidth),
            Math.Min((int)Math.Round(480 * scale), maxHeight));
        ClientSize = new Size(
            Math.Min((int)Math.Round(720 * scale), maxWidth),
            Math.Min((int)Math.Round(560 * scale), maxHeight));
        PositionAtBottomRight();
        if (autoStarted) Opacity = 0;

        titleBar = new TitleBar { AppIcon = Icon, Title = "SillyTavern 管理器", Version = "v" + ManagerVersion.Current };
        titleBar.SettingsClicked += (_, _) => ShowSettings();
        titleBar.MinimizeClicked += (_, _) => HideToTray();
        titleBar.CloseClicked += (_, _) => HideToTray();

        root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Current.Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(36)));
        Controls.Add(root);
        root.Controls.Add(titleBar, 0, 0);

        content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Background, Padding = new Padding(S(20), S(16), S(20), 0) };
        root.Controls.Add(content, 0, 1);

        layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Theme.Current.Background };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(124)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(86)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, S(78)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        bannerRow = layout.RowStyles[1];
        logRow = layout.RowStyles[4];
        onboardingRow = layout.RowStyles[5];
        content.Controls.Add(layout);

        statusCard = BuildStatusCard();
        layout.Controls.Add(statusCard, 0, 0);

        banner = new RoundedPanel { Dock = DockStyle.Fill, Radius = S(8), FillColor = Theme.Current.AccentSoft, BorderColor = Draw.Blend(Theme.Current.Accent, Theme.Current.Border, 0.7f), Padding = new Padding(S(14), S(8), S(8), S(8)), Visible = false };
        bannerLabel = new ThemedLabel { Tone = LabelTone.Secondary, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
        bannerAction = new ThemedButton { Text = "立即重启并更新", Variant = ButtonVariant.Quiet, PreferredHeight = 28, IconGlyph = Icons.Power, Backdrop = Theme.Current.AccentSoft };
        bannerAction.Click += async (_, _) => await ExitAsync(restartAfterInstall: true);
        bannerClose = new ThemedIconButton { Glyph = Icons.Close, GlyphSize = 12, Size = new Size(S(28), S(28)), Backdrop = Theme.Current.AccentSoft, Text = "忽略管理器更新提示" };
        bannerClose.AccessibleName = "忽略管理器更新提示";
        bannerClose.Click += (_, _) => { bannerDismissed = true; UpdateDisplay(); };
        var bannerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bannerGrid.Controls.Add(bannerLabel, 0, 0);
        bannerGrid.Controls.Add(bannerAction, 1, 0);
        bannerGrid.Controls.Add(bannerClose, 2, 0);
        banner.Controls.Add(bannerGrid);
        layout.Controls.Add(banner, 0, 1);

        layout.Controls.Add(BuildAddressSection(), 0, 2);
        layout.Controls.Add(BuildMaintenanceSection(), 0, 3);

        logSection = BuildLogSection();
        layout.Controls.Add(logSection, 0, 4);
        onboarding = BuildOnboarding();
        layout.Controls.Add(onboarding, 0, 5);

        footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Background };
        footer.Paint += (_, e) => { using var pen = new Pen(Theme.Current.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var footerGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(S(14), 0, S(14), 0) };
        footerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var footerLinks = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        footerLinks.Controls.Add(FooterLink("日志目录", (_, _) => OpenPath(log.DirectoryPath, create: true)));
        footerLinks.Controls.Add(FooterLink("打开配置", (_, _) => OpenPath(Path.Combine(settings.InstallDirectory, "config.yaml"))));
        footerLinks.Controls.Add(FooterLink("数据目录", (_, _) => OpenPath(server.Config?.DataRoot ?? Path.Combine(settings.InstallDirectory, "data"))));
        var quitButton = new ThemedButton { Text = "退出程序", Variant = ButtonVariant.Quiet, PreferredHeight = 26, Font = Typography.Caption, Backdrop = Theme.Current.Background, DangerHover = true, Margin = new Padding(0) };
        quitButton.Click += async (_, _) => await ExitAsync();
        footerGrid.Controls.Add(footerLinks, 0, 0);
        footerGrid.Controls.Add(quitButton, 1, 0);
        footer.Controls.Add(footerGrid);
        root.Controls.Add(footer, 0, 2);

        toast.Parent = this;
        Controls.Add(toast);
        toast.BringToFront();

        trayMenu.Renderer = new TrayMenuRenderer();
        trayMenu.ShowImageMargin = true;
        trayMenu.ImageScalingSize = new Size(S(16), S(16));
        trayMenu.Font = Typography.Body;
        trayMenu.DropShadowEnabled = true;
        trayMenu.Opened += (_, _) => Dwm.Apply(trayMenu.Handle);
        BuildTrayMenu();
        tray = new NotifyIcon { Visible = true, ContextMenuStrip = trayMenu, Text = "SillyTavern 管理器" };
        tray.DoubleClick += (_, _) => RestoreWindow();
        SetTrayIcon(TrayStatus.ColorFor(StatusTone.Stopped), force: true);

        logLines.AddRange(log.ReadRecent(2000));
        log.LineWritten += OnLogLine;
        server.Changed += OnServerChanged;
        server.Notice += message => TryUi(() => Notify("SillyTavern 启动缓慢", message));
        PendingManagerUpdateStore.Changed += OnPendingUpdateStoreChanged;
        maintenance.StepChanged += (index, status, _) => TryUi(() => OnStepChanged(index, status));
        logTimer.Tick += (_, _) => FlushLogQueue();
        logTimer.Start();
        busyTimer.Tick += (_, _) => UpdateBusyElapsed();
        busyTimer.Start();
        watchdogTimer.Tick += (_, _) => CheckWatchdog();
        watchdogTimer.Start();
        managerUpdateTimer.Tick += async (_, _) => await CheckManagerUpdateAsync(manual: false);
        RebuildLog();
        UpdateDisplay();
        // The status title doubles as the screen-reader live region (v2 §8).
        statusLabel.LiveSetting = AutomationLiveSetting.Polite;
        progressLabel.LiveSetting = AutomationLiveSetting.Polite;

        Shown += async (_, _) =>
        {
            PositionAtBottomRight();
            PositionToast();
            if (autoStarted) { HideToTray(); Opacity = 1; }
            await InitializeAsync();
            if (settings.AutoUpdateManager)
            {
                managerUpdateTimer.Start();
                try { await Task.Delay(TimeSpan.FromSeconds(5), managerUpdateLifetime.Token); await CheckManagerUpdateAsync(manual: false); }
                catch (OperationCanceledException) { }
            }
        };
        Resize += (_, _) => { PositionToast(); UpdateCompactMode(); if (WindowState == FormWindowState.Minimized) HideToTray(); };
        _ = Task.Run(() =>
        {
            // Polling with a timeout keeps this thread from sitting in an unbreakable wait that
            // races Dispose; an escaping ObjectDisposedException here would kill the whole process.
            while (!stopSignalThread)
            {
                try
                {
                    if (!showSignal.WaitOne(250) || stopSignalThread) continue;
                    TryUi(RestoreWindow);
                }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    private int S(int logical) => Draw.Scale(this, logical);
    private static readonly Padding Fill = new(0);

    private async Task InitializeAsync()
    {
        var pending = ReadPendingUpdate();
        if (pending is { Failed: true })
        {
            log.Write("管理器更新", "上次自动安装失败，可从托盘手动检查管理器更新后重试。");
            Notify("管理器更新未安装", "请查看日志，或从托盘手动检查管理器更新。");
        }
        if (CurrentConfiguration() == ConfigurationState.Unconfigured)
        {
            configReadFailed = false;
            UpdateDisplay();
            return;
        }
        await RunExclusiveAsync(BusyOperation.ReadingConfig, async () =>
        {
            // The branch lookup is an independent git spawn; overlap it with the start path.
            var branchTask = maintenance.GetBranchAsync();
            if (settings.StartServerOnLaunch) await server.StartAsync(autoStarted);
            else await server.RefreshConfigAsync();
            branch = await branchTask;
        }, showErrors: !autoStarted);
        UpdateDisplay();
    }

    private async Task RunExclusiveAsync(BusyOperation operation, Func<Task> action, bool showErrors = true)
    {
        if (busyOperation != BusyOperation.None || reallyClosing) return;
        busyOperation = operation;
        busySinceUtc = DateTime.UtcNow;
        if (operation is BusyOperation.Updating or BusyOperation.Repairing) maintenanceCancel = new CancellationTokenSource();
        UpdateDisplay();
        try
        {
            await action();
            if (operation == BusyOperation.ReadingConfig) configReadFailed = false;
            if (operation is BusyOperation.Updating or BusyOperation.Repairing)
                lastMaintenance = (operation == BusyOperation.Updating ? "更新完成" : "依赖修复完成", DateTime.Now);
        }
        catch (OperationCanceledException ex)
        {
            // A cancel raised by the maintenance cancel button gets a friendly toast;
            // other cancels (stop declined, watchdog) keep their specific message.
            var userCancelled = maintenanceCancel is { IsCancellationRequested: true };
            var message = userCancelled
                ? operation == BusyOperation.Updating ? "已取消更新" : operation == BusyOperation.Repairing ? "已取消修复" : "操作已取消"
                : ex.Message;
            log.Write("管理器", message);
            if (showErrors) ShowToast(message, ToastKind.Info);
        }
        catch (Exception ex)
        {
            log.Write("管理器", ex.Message);
            if (operation == BusyOperation.ReadingConfig && server.Config is null) configReadFailed = true;
            if (showErrors) ShowToast(ex.Message, ToastKind.Warning);
            else Notify("SillyTavern 启动失败", ex.Message);
        }
        finally
        {
            busyOperation = BusyOperation.None;
            currentSteps = [];
            currentStepIndex = 0;
            maintenanceCancel?.Dispose();
            maintenanceCancel = null;
            if (!IsDisposed) UpdateDisplay();
        }
    }

    private async Task ExitAsync(bool restartAfterInstall = false)
    {
        if (busyOperation != BusyOperation.None)
        {
            // A maintenance preflight can still be cancelled; offer the exit path instead of refusing (v2 §7).
            var cancellable = busyOperation is BusyOperation.Updating or BusyOperation.Repairing
                && currentStepIndex == 0
                && maintenanceCancel is { IsCancellationRequested: false };
            if (!cancellable)
            {
                ShowToast("请等待当前操作结束后再退出。", ToastKind.Info);
                return;
            }
            if (!ConfirmDialog.AskConfirm(this, "退出程序",
                "将取消当前的维护操作并退出。\n若服务正在运行，退出前会先停止服务。", "取消并退出")) return;
            maintenanceCancel?.Cancel();
            for (var i = 0; i < 300 && busyOperation != BusyOperation.None; i++) await Task.Delay(100);
            if (busyOperation != BusyOperation.None)
            {
                ShowToast("当前操作尚未结束，请稍后再退出。", ToastKind.Warning);
                return;
            }
        }
        var canClose = false;
        await RunExclusiveAsync(BusyOperation.Exiting, async () =>
        {
            canClose = !server.HasOwnedProcess || await server.StopAsync(AskStopDecisionAsync);
        });
        if (!canClose) return;
        managerUpdateLifetime.Cancel();
        var updateReady = managerUpdates is null || await managerUpdates.PrepareForExitAsync();
        if (updateReady) ManagerUpdateInstaller.TryLaunch(restart: restartAfterInstall, autostart: false);
        reallyClosing = true;
        Close();
    }

    private void ShowSettings()
    {
        if (busyOperation != BusyOperation.None) return;
        using var dialog = new SettingsForm(settings, server.HasOwnedProcess, () => server.HasOwnedProcess);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Theme.ApplyAppearance(settings.Appearance);
        if (settings.AutoUpdateManager) { managerUpdateTimer.Start(); _ = CheckManagerUpdateAsync(manual: false); }
        else
        {
            managerUpdateTimer.Stop();
            // Cancel only: disposing a source that an in-flight check still references turns
            // into ObjectDisposedException noise logged as a bogus "检查失败".
            autoUpdateCheck.Cancel();
            autoUpdateCheck = new CancellationTokenSource();
        }
        _ = RefreshEnvironmentAsync();
    }

    private ConfigurationState CurrentConfiguration()
    {
        if (server.Config is not null) return ConfigurationState.Configured;
        var directory = settings.InstallDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "server.js")))
            return ConfigurationState.Unconfigured;
        if (configReadFailed) return ConfigurationState.Error;
        return ConfigurationState.Pending;
    }

    private void PositionAtBottomRight()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - S(12)), Math.Max(area.Top, area.Bottom - Height - S(12)));
    }

    private void HideToTray()
    {
        hiddenToTray = true;
        Hide();
        WindowState = FormWindowState.Normal;
        // One-time hint promised by the phase-1 design; only on an explicit hide from a visible window.
        if (firstTrayHide && !autoStarted)
        {
            firstTrayHide = false;
            tray.BalloonTipTitle = "SillyTavern 管理器仍在后台运行";
            tray.BalloonTipText = "窗口已收起到系统托盘，双击托盘图标可随时打开；服务不受影响。";
            tray.ShowBalloonTip(3000);
        }
    }

    private void RestoreWindow()
    {
        if (IsDisposed) return;
        var wasHidden = hiddenToTray;
        hiddenToTray = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        PositionToast();
        if (wasHidden) RebuildLog();
    }

    private void TryUi(Action action)
    {
        if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }

    private void ShowToast(string text, ToastKind kind)
    {
        if (IsDisposed) return;
        if (Visible)
        {
            toast.Show(text, kind);
            PositionToast();
        }
        else Notify("SillyTavern 管理器", text);
    }

    private void Notify(string title, string text)
    {
        tray.BalloonTipTitle = title;
        tray.BalloonTipText = text;
        tray.ShowBalloonTip(5000);
    }

    private void PositionToast()
    {
        if (toast is null || toast.Width <= 0) return;
        toast.Location = new Point(
            Math.Max(S(8), ClientSize.Width - toast.Width - S(16)),
            titleBar.Bottom + S(12));
    }

    private void OpenPath(string path, bool create = false)
    {
        try
        {
            if (create) Directory.CreateDirectory(path);
            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("路径不存在。", path);
            System.Diagnostics.Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast("无法打开：" + ex.Message, ToastKind.Warning); }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (logSearch.ContainsFocus) { logSearch.Clear(); return true; }
            HideToTray();
            return true;
        }
        if (keyData == (Keys.Control | Keys.F))
        {
            if (compactMode) OpenSearchPopup();
            else logSearch.FocusInput();
            return true;
        }
        if (keyData == (Keys.Control | Keys.End)) { ResumeScroll(); return true; }
        if (keyData == Keys.F5) { _ = RefreshEnvironmentAsync(); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { ShowSettings(); return true; }
        if (keyData == Keys.Enter)
        {
            var focused = Control.FromHandle(GetFocus());
            // Let any focused control handle Enter itself; only use the form-level
            // shortcut when the window (not a child control) owns focus.
            if (focused is not null && focused != this) return base.ProcessCmdKey(ref msg, keyData);
            if (primaryButton.Visible && primaryButton.Enabled)
            {
                primaryButton.PerformClick();
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void ApplyTheme()
    {
        if (root is null) return;
        var p = Theme.Current;
        BackColor = p.Border;
        root.BackColor = p.Background;
        content.BackColor = p.Background;
        layout.BackColor = p.Background;
        footer.BackColor = p.Background;
        logSurface.FillColor = p.Surface;
        logSurface.BorderColor = p.Border;
        banner.FillColor = p.AccentSoft;
        banner.BorderColor = Draw.Blend(p.Accent, p.Border, 0.7f);
        bannerAction.Backdrop = p.AccentSoft;
        bannerClose.Backdrop = p.AccentSoft;
        backToBottom.Backdrop = p.Surface;
        onboarding.FillColor = p.Surface;
        onboarding.BorderColor = p.Border;
        logView.ApplyTheme();
        foreach (var (item, glyph) in trayGlyphs)
        {
            var old = item.Image;
            item.Image = Icons.ToBitmap(glyph, S(16), item.Tag as string == "danger" ? p.DangerText : p.TextSecondary);
            old?.Dispose();
        }
        trayMenu.Invalidate();
        RebuildLog();
        UpdateDisplay();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref Point position);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyClosing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
        // Windows shutdown/logoff and task-manager closes fall through here; without this stop
        // the server used to survive as an orphan the next launch cannot manage.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing or CloseReason.ApplicationExitCall)
            server.EmergencyStopSync();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The signal thread notices the flag within 250 ms; no wake-up event is needed and
            // touching the handle here would race the polling loop above.
            stopSignalThread = true;
            logTimer.Stop(); logTimer.Dispose();
            logSearchTimer.Stop(); logSearchTimer.Dispose();
            watchdogTimer.Stop(); watchdogTimer.Dispose();
            managerUpdateTimer.Stop(); managerUpdateTimer.Dispose();
            busyTimer.Stop(); busyTimer.Dispose();
            if (searchPopup is { IsDisposed: false }) searchPopup.Dispose();
            maintenanceCancel?.Dispose();
            managerUpdateLifetime.Cancel(); autoUpdateCheck.Cancel(); managerUpdates?.Dispose();
            log.LineWritten -= OnLogLine; server.Changed -= OnServerChanged; PendingManagerUpdateStore.Changed -= OnPendingUpdateStoreChanged;
            tray.Visible = false; tray.Dispose(); activeTrayIcon?.Dispose(); trayMenu.Dispose(); toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
