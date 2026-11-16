using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms.Automation;
using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;
using SillyTavernTray.Ui.Dialogs;

namespace SillyTavernTray;

internal sealed class MainForm : ThemedForm
{
    private readonly bool autoStarted;
    private readonly EventWaitHandle showSignal;
    private readonly AppSettings settings;
    private readonly AppLog log = new();
    private readonly ServerController server;
    private readonly MaintenanceService maintenance;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly TrayHeaderPanel trayHeader = new();
    private readonly ToolStripMenuItem trayOpenServer = new("打开 SillyTavern");
    private readonly ToolStripMenuItem trayOpenWindow = new("打开管理窗口");
    private readonly ToolStripMenuItem trayStartStop = new("启动服务");
    private readonly ToolStripMenuItem trayRestart = new("重启服务");
    private readonly ToolStripMenuItem trayUpdate = new("检查 ST 更新");
    private readonly ToolStripMenuItem trayManagerUpdate = new("检查管理器更新");
    private readonly ToolStripMenuItem trayRepair = new("依赖修复");
    private readonly ToolStripMenuItem trayDiagnostics = new("复制诊断");
    private readonly ToolStripMenuItem traySettings = new("设置");
    private readonly ToolStripMenuItem trayExit = new("退出（并停止服务）");
    private readonly Dictionary<ToolStripMenuItem, string> trayGlyphs = new();
    private readonly ToolTip toolTip = new() { InitialDelay = 450, ReshowDelay = 100 };

    private readonly TableLayoutPanel root;
    private readonly Panel content;
    private readonly TableLayoutPanel layout;
    private readonly TitleBar titleBar;
    private readonly Panel footer;

    private readonly RoundedPanel statusCard;
    private TableLayoutPanel statusGrid = null!;
    private StatusDot statusDot = null!;
    private ThemedLabel statusLabel = null!;
    private ThemedLabel metaLabel = null!;
    private readonly FlowLayoutPanel statusActions = new();
    private readonly ThemedButton primaryButton = new();
    private readonly FlowLayoutPanel progressFlow = new();
    private readonly ThemedLabel progressLabel = new() { Tone = LabelTone.Warning };
    private readonly Spinner spinner = new();

    private readonly RoundedPanel banner;
    private readonly ThemedLabel bannerLabel;
    private readonly ThemedButton bannerAction;
    private readonly ThemedIconButton bannerClose;
    private RowStyle? bannerRow;

    private readonly AddressPicker addressPicker = new();
    private ThemedButton copyAddressButton = null!;
    private readonly List<AddressItem> addressItems = new();
    private int selectedAddress;

    private ThemedButton checkUpdateButton = null!;
    private ThemedButton updateButton = null!;
    private ThemedButton repairButton = null!;

    private readonly Panel logSection;
    private ThemedLabel logCount = null!;
    private readonly SegmentedControl logSources = new();
    private readonly SearchBox logSearch = new();
    private ThemedIconButton pauseButton = null!;
    private ThemedIconButton copyLogButton = null!;
    private ThemedIconButton copyDiagButton = null!;
    private ThemedIconButton searchToggleButton = null!;
    private ThemedIconButton openLogButton = null!;
    private readonly LogRichTextBox logView = new();
    private ThemedButton backToBottom = null!;
    private RoundedPanel logSurface = null!;
    private SearchPopup? searchPopup;
    private readonly List<(int Offset, int Length)> searchMatches = [];
    private int searchMatchIndex = -1;
    private readonly List<(string Source, bool IsSeparator)> renderedEntries = [];
    private string? lastRenderedDate;
    private bool compactMode;

    private readonly StepProgress stepProgress = new();
    private readonly FlowLayoutPanel cancelFlow = new();
    private readonly ThemedButton cancelMaintenanceButton = new();
    private readonly ThemedLabel cancelHintLabel = new() { Tone = LabelTone.Tertiary, AutoSize = true };
    private readonly ThemedLabel elapsedLabel = new() { Tone = LabelTone.Tertiary, AutoSize = true };
    private readonly ThemedLabel watchdogBadge = new() { Tone = LabelTone.Success, AutoSize = true, Visible = false, BackColor = Color.Transparent };
    private readonly System.Windows.Forms.Timer busyTimer = new() { Interval = 1000 };
    private DateTime busySinceUtc = DateTime.MinValue;
    private List<StepState> currentSteps = [];
    private int currentStepIndex;
    private CancellationTokenSource? maintenanceCancel;
    private int watchdogRestartCount;
    private DateTime? lastWatchdogRestartAt;
    private TimeSpan? lastRunDuration;
    private (string Kind, DateTime Time)? lastMaintenance;
    private (int ProcessId, string Name)? occupierInfo;
    private DateTime occupierQueriedUtc = DateTime.MinValue;

    private readonly RoundedPanel onboarding;
    private RowStyle? logRow;
    private RowStyle? onboardingRow;

    private readonly ToastHost toast = new();

    private readonly ConcurrentQueue<string> queuedLines = new();
    private readonly List<string> logLines = new();
    private readonly System.Windows.Forms.Timer logTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer logSearchTimer = new() { Interval = 220 };
    private readonly System.Windows.Forms.Timer watchdogTimer = new() { Interval = 10_000 };
    private readonly System.Windows.Forms.Timer managerUpdateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly CancellationTokenSource managerUpdateLifetime = new();
    private CancellationTokenSource autoUpdateCheck = new();
    private ManagerUpdateService? managerUpdates;
    private string? lastUpdateNotification;
    private int omittedLiveLines;
    private Icon? activeTrayIcon;
    private Color lastTrayColor = Color.Empty;
    private BusyOperation busyOperation;
    private bool reallyClosing, pausedScroll, bannerDismissed, configReadFailed;
    private volatile bool stopSignalThread;
    private bool hiddenToTray, heartbeatUnavailableLogged;
    private readonly RestartLimiter watchdogLimiter = new();
    private DateTime? runningSinceUtc;
    private DateTime watchdogPausedUntilUtc = DateTime.MinValue;
    private ServerState previousServerState = ServerState.Stopped;
    private bool watchdogRestarting;
    private string branch = "未知";
    private PrimaryActionKind primaryKind;
    // Cached display inputs: UpdateDisplay runs on every state change and must not touch the disk.
    private PendingManagerUpdate? pendingUpdateCache;
    private bool pendingUpdateCacheValid;
    private InstanceConfig? lastAddressConfig;
    private DateTime lastAddressRefreshUtc = DateTime.MinValue;
    private IReadOnlyList<ViewAction>? lastActionSet;
    private StatusTone? lastActionTone;

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
        SetTrayIcon(Color.FromArgb(140, 146, 154), force: true);

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

    private RoundedPanel BuildStatusCard()
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = S(10),
            Padding = new Padding(S(16), S(14), S(16), S(14)),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, S(12))
        };

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, BackColor = Color.Transparent };
        statusGrid = grid;
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        statusDot = new StatusDot { Margin = new Padding(0, S(5), S(8), 0), BackColor = Color.Transparent };
        statusLabel = new ThemedLabel { Font = Typography.Status, Tone = LabelTone.Primary, Text = "已停止", AutoSize = true, Margin = Fill };
        head.Controls.Add(statusDot);
        head.Controls.Add(statusLabel);

        // Meta row: primary metadata (ellipsis) + optional watchdog badge (v2 §4.1).
        var metaRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Fill };
        metaRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        metaRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        metaLabel = new ThemedLabel
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Tone = LabelTone.Secondary,
            Font = Typography.Caption,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = Fill
        };
        watchdogBadge.Margin = new Padding(S(6), S(3), 0, 0);
        metaRow.Controls.Add(metaLabel, 0, 0);
        metaRow.Controls.Add(watchdogBadge, 1, 0);

        statusActions.Dock = DockStyle.Fill;
        statusActions.FlowDirection = FlowDirection.LeftToRight;
        statusActions.WrapContents = false;
        statusActions.BackColor = Color.Transparent;
        statusActions.Margin = new Padding(-S(8), 0, 0, 0);

        primaryButton.Variant = ButtonVariant.Accent;
        primaryButton.PreferredHeight = 34;
        primaryButton.AutoSize = true;
        primaryButton.Margin = Fill;
        primaryButton.Anchor = AnchorStyles.Right;
        primaryButton.Click += async (_, _) => await RunPrimaryAsync();

        progressFlow.AutoSize = true;
        progressFlow.WrapContents = false;
        progressFlow.BackColor = Color.Transparent;
        progressFlow.Margin = Fill;
        progressFlow.Anchor = AnchorStyles.Left;
        progressFlow.Visible = false;
        spinner.Margin = new Padding(0, S(8), S(8), 0);
        progressLabel.AutoSize = true;
        progressLabel.Margin = new Padding(0, S(7), S(6), 0);
        elapsedLabel.AutoSize = true;
        elapsedLabel.Margin = new Padding(0, S(7), 0, 0);
        progressFlow.Controls.Add(spinner);
        progressFlow.Controls.Add(progressLabel);
        progressFlow.Controls.Add(elapsedLabel);

        stepProgress.Margin = Fill;
        cancelFlow.AutoSize = true;
        cancelFlow.WrapContents = false;
        cancelFlow.BackColor = Color.Transparent;
        cancelFlow.Margin = Fill;
        cancelFlow.Visible = false;
        cancelMaintenanceButton.Text = "取消";
        cancelMaintenanceButton.Variant = ButtonVariant.Quiet;
        cancelMaintenanceButton.PreferredHeight = 24;
        cancelMaintenanceButton.Font = Typography.Caption;
        cancelMaintenanceButton.Margin = new Padding(0, 0, S(8), 0);
        cancelMaintenanceButton.Click += (_, _) => CancelMaintenance();
        cancelHintLabel.Margin = new Padding(0, S(5), 0, 0);
        cancelFlow.Controls.Add(cancelMaintenanceButton);
        cancelFlow.Controls.Add(cancelHintLabel);

        grid.Controls.Add(head, 0, 0);
        grid.Controls.Add(metaRow, 0, 1);
        grid.Controls.Add(statusActions, 0, 2);
        grid.Controls.Add(progressFlow, 0, 3);
        grid.Controls.Add(stepProgress, 0, 4);
        grid.Controls.Add(cancelFlow, 0, 5);
        grid.Controls.Add(primaryButton, 1, 0);
        grid.SetRowSpan(primaryButton, 3);
        card.Controls.Add(grid);
        return card;
    }

    private Control BuildAddressSection()
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, S(12)) };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new ThemedLabel { Text = "访问地址", Font = Typography.Section, Tone = LabelTone.Secondary, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Margin = Fill };
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Fill };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(54)));
        addressPicker.Dock = DockStyle.Fill;
        addressPicker.Margin = new Padding(0, 0, S(8), 0);
        addressPicker.Click += (_, _) => ShowAddressMenu();
        copyAddressButton = new ThemedButton
        {
            Variant = ButtonVariant.Secondary,
            IconGlyph = Icons.Copy,
            Text = "复制",
            Caption = "",
            PreferredHeight = 40,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = Fill,
            AccessibleName = "复制地址"
        };
        copyAddressButton.Click += (_, _) => CopyAddress();
        toolTip.SetToolTip(copyAddressButton, "复制地址");
        row.Controls.Add(addressPicker, 0, 0);
        row.Controls.Add(copyAddressButton, 1, 0);
        section.Controls.Add(title, 0, 0);
        section.Controls.Add(row, 0, 1);
        return section;
    }

    private Control BuildMaintenanceSection()
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, S(12)) };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new ThemedLabel { Text = "更新与维护", Font = Typography.Section, Tone = LabelTone.Secondary, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Margin = Fill };
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        checkUpdateButton = MaintenanceButton("检查更新", Icons.Restart, async (_, _) => await CheckUpdatesAsync());
        updateButton = MaintenanceButton("更新 ST", Icons.Download, async (_, _) => await UpdateAsync());
        repairButton = MaintenanceButton("依赖修复", Icons.Repair, async (_, _) => await RepairAsync());
        updateButton.DangerHover = true;
        repairButton.DangerHover = true;
        toolTip.SetToolTip(updateButton, "更新会停止服务并中断正在进行的聊天");
        toolTip.SetToolTip(repairButton, "依赖修复会停止服务并重新安装生产依赖");
        row.Controls.Add(checkUpdateButton);
        row.Controls.Add(updateButton);
        row.Controls.Add(repairButton);
        section.Controls.Add(title, 0, 0);
        section.Controls.Add(row, 0, 1);
        return section;
    }

    private ThemedButton MaintenanceButton(string text, string glyph, EventHandler handler)
    {
        var button = new ThemedButton { Text = text, Variant = ButtonVariant.Secondary, IconGlyph = glyph, PreferredHeight = 32, Margin = new Padding(0, 0, S(8), 0) };
        button.Click += handler;
        button.AccessibleName = text;
        return button;
    }

    private Panel BuildLogSection()
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Margin = Fill };
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));
        section.RowStyles.Add(new RowStyle(SizeType.Absolute, S(8)));
        section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Fill };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titleFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        var title = new ThemedLabel { Text = "日志", Font = Typography.Section, Tone = LabelTone.Secondary, AutoSize = true, Margin = new Padding(0, S(6), S(8), 0) };
        logCount = new ThemedLabel { Font = Typography.Caption, Tone = LabelTone.Tertiary, AutoSize = true, Visible = false, Margin = new Padding(0, S(8), 0, 0) };
        titleFlow.Controls.Add(title);
        titleFlow.Controls.Add(logCount);

        var tools = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        openLogButton = LogIcon(Icons.Folder, "打开日志目录", (_, _) => OpenPath(log.DirectoryPath, create: true));
        copyLogButton = LogIcon(Icons.Copy, "复制日志", (_, _) => CopyLog());
        copyDiagButton = LogIcon(Icons.History, "复制诊断信息（版本、状态、最近日志）", (_, _) => CopyDiagnostics());
        pauseButton = LogIcon(Icons.Pause, "暂停滚动", (_, _) => TogglePause());
        searchToggleButton = LogIcon(Icons.Search, "搜索日志", (_, _) => OpenSearchPopup());
        searchToggleButton.Visible = false;
        logSearch.Width = S(140);
        logSearch.Margin = new Padding(0, S(2), S(4), 0);
        logSources.AccessibleName = "日志来源";
        logSources.Items = ["全部", "服务", "维护", "管理器"];
        logSources.SelectedIndex = 0;
        logSources.Width = logSources.MeasureWidth();
        logSources.Margin = new Padding(0, S(3), S(4), 0);
        logSources.SelectedIndexChanged += (_, _) => RebuildLog();
        logSearch.TextChanged += (_, _) => { logSearchTimer.Stop(); logSearchTimer.Start(); };
        logSearchTimer.Tick += (_, _) => { logSearchTimer.Stop(); RebuildLog(); };
        logSearch.NavigateRequested += forward => NavigateSearch(forward ? 1 : -1);
        tools.Controls.Add(openLogButton);
        tools.Controls.Add(copyLogButton);
        tools.Controls.Add(copyDiagButton);
        tools.Controls.Add(pauseButton);
        tools.Controls.Add(searchToggleButton);
        tools.Controls.Add(logSearch);
        tools.Controls.Add(logSources);
        header.Controls.Add(titleFlow, 0, 0);
        header.Controls.Add(tools, 1, 0);

        logSurface = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = S(10),
            Padding = new Padding(S(8), S(8), S(8), S(8)),
            FillColor = Theme.Current.Surface,
            BorderColor = Theme.Current.Border,
            Margin = Fill
        };
        logView.Dock = DockStyle.Fill;
        logView.ScrolledUp += PauseScroll;
        logSurface.Controls.Add(logView);
        backToBottom = new ThemedButton
        {
            Text = "回到底部",
            Variant = ButtonVariant.Secondary,
            IconGlyph = Icons.Down,
            PreferredHeight = 28,
            Visible = false,
            Backdrop = Theme.Current.Surface
        };
        backToBottom.Click += (_, _) => ResumeScroll();
        logSurface.Controls.Add(backToBottom);
        backToBottom.BringToFront();
        logSurface.Resize += (_, _) => PositionBackToBottom();

        section.Controls.Add(header, 0, 0);
        section.Controls.Add(logSurface, 0, 2);
        return section;
    }

    private ThemedIconButton LogIcon(string glyph, string tooltip, EventHandler handler)
    {
        var button = new ThemedIconButton { Glyph = glyph, GlyphSize = 16, Margin = new Padding(0, S(1), S(2), 0), Text = tooltip };
        button.AccessibleName = tooltip;
        button.Click += handler;
        toolTip.SetToolTip(button, tooltip);
        return button;
    }

    private RoundedPanel BuildOnboarding()
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, Radius = S(10), FillColor = Theme.Current.Surface, BorderColor = Theme.Current.Border, Padding = new Padding(S(36), S(36), S(36), S(36)), Visible = false, Margin = Fill };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Color.Transparent };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(32)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(16)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(38)));
        var heading = new ThemedLabel { Text = "选择 SillyTavern 目录", Font = Typography.Title, Tone = LabelTone.Primary, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Fill };
        var description = new ThemedLabel
        {
            Text = "目录中需要包含 server.js。管理器不会修改现有配置。",
            Font = Typography.Body,
            Tone = LabelTone.Secondary,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Fill
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        var browse = new ThemedButton { Text = "浏览…", Variant = ButtonVariant.Accent, IconGlyph = Icons.Folder, PreferredHeight = 34, Margin = new Padding(0, 0, S(8), 0) };
        browse.Click += async (_, _) => await ChooseInstallDirectoryAsync();
        var viewLog = new ThemedButton { Text = "查看日志", Variant = ButtonVariant.Quiet, PreferredHeight = 34, Margin = new Padding(0, 0, S(8), 0) };
        viewLog.Click += (_, _) => OpenPath(log.DirectoryPath, create: true);
        var learnMore = new ThemedButton { Text = "了解管理器能做什么", Variant = ButtonVariant.Quiet, PreferredHeight = 34 };
        learnMore.Click += (_, _) => OpenReadme();
        actions.Controls.Add(browse);
        actions.Controls.Add(viewLog);
        actions.Controls.Add(learnMore);
        grid.Controls.Add(heading, 0, 0);
        grid.Controls.Add(description, 0, 1);
        grid.Controls.Add(actions, 0, 4);
        card.Controls.Add(grid);
        return card;
    }

    private ThemedButton FooterLink(string text, EventHandler handler)
    {
        var button = new ThemedButton { Text = text, Variant = ButtonVariant.Quiet, PreferredHeight = 26, Font = Typography.Caption, Backdrop = Theme.Current.Background, Margin = new Padding(0, 0, S(4), 0) };
        button.Click += handler;
        return button;
    }

    private void BuildTrayMenu()
    {
        trayMenu.Items.Clear();
        trayMenu.Items.Add(new ToolStripControlHost(trayHeader)
        {
            AutoSize = false,
            Size = new Size(S(248), S(46)),
            Margin = Padding.Empty,
            Padding = Padding.Empty
        });
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(trayOpenServer, Icons.OpenExternal, (_, _) => OpenServer(), bold: true);
        AddTrayItem(trayOpenWindow, Icons.AppWindow, (_, _) => RestoreWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(trayStartStop, Icons.Play, async (_, _) => await StartOrStopAsync());
        AddTrayItem(trayRestart, Icons.Restart, async (_, _) => await RestartAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(trayUpdate, Icons.Restart, async (_, _) => await CheckUpdatesAsync(), shortcutHint: "F5");
        AddTrayItem(trayManagerUpdate, Icons.Download, async (_, _) => await CheckManagerUpdateAsync(manual: true));
        AddTrayItem(trayRepair, Icons.Repair, async (_, _) => await RepairAsync());
        AddTrayItem(trayDiagnostics, Icons.History, (_, _) => CopyDiagnostics());
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(traySettings, Icons.Settings, (_, _) => ShowSettings(), shortcutHint: "Ctrl+,");
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(trayExit, Icons.Power, async (_, _) => await ExitAsync(), danger: true);
    }

    private void AddTrayItem(ToolStripMenuItem item, string glyph, EventHandler handler, bool danger = false, bool bold = false, string? shortcutHint = null)
    {
        item.AutoSize = false;
        item.Width = S(248);
        item.Height = S(32);
        item.Padding = Padding.Empty;
        item.Font = bold ? Typography.ButtonBold : Typography.Body;
        item.ShowShortcutKeys = false;
        item.ShortcutKeyDisplayString = shortcutHint;
        item.Tag = (danger ? "danger" : null, shortcutHint);
        item.Image = Icons.ToBitmap(glyph, S(16), danger ? Theme.Current.DangerText : Theme.Current.TextSecondary);
        item.ImageScaling = ToolStripItemImageScaling.None;
        item.Click += handler;
        trayGlyphs[item] = glyph;
        trayMenu.Items.Add(item);
    }

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

    private async Task StartOrStopAsync() => await RunExclusiveAsync(
        server.HasOwnedProcess ? BusyOperation.Stopping : BusyOperation.Starting,
        async () =>
        {
            ClearWatchdogBadge();
            if (server.HasOwnedProcess) await server.StopAsync(AskStopDecisionAsync);
            else
            {
                var branchTask = maintenance.GetBranchAsync();
                await server.StartAsync();
                branch = await branchTask;
            }
        });

    private async Task RestartAsync()
    {
        ClearWatchdogBadge();
        if (server.HasOwnedProcess)
        {
            var stopped = false;
            await RunExclusiveAsync(BusyOperation.Stopping, async () => { stopped = await server.StopAsync(AskStopDecisionAsync); });
            if (!stopped) return;
        }
        await RunExclusiveAsync(BusyOperation.Starting, async () =>
        {
            var branchTask = maintenance.GetBranchAsync();
            await server.StartAsync();
            branch = await branchTask;
        });
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

    private async Task RunPrimaryAsync()
    {
        switch (primaryKind)
        {
            case PrimaryActionKind.OpenServer: OpenServer(); break;
            case PrimaryActionKind.Start: await StartOrStopAsync(); break;
            case PrimaryActionKind.Retry:
                // In the occupied state "retry" must re-check the port and start when it is free.
                if (server.State == ServerState.Occupied) await StartOrStopAsync();
                else await RefreshEnvironmentAsync();
                break;
            case PrimaryActionKind.ChooseDirectory: await ChooseInstallDirectoryAsync(); break;
        }
    }

    private async Task ChooseInstallDirectoryAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 server.js 的 SillyTavern 目录",
            SelectedPath = Directory.Exists(settings.InstallDirectory) ? settings.InstallDirectory : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = Path.GetFullPath(dialog.SelectedPath.Trim());
        if (!File.Exists(Path.Combine(path, "server.js")))
        {
            ShowToast("该目录中没有 server.js，请选择已有的 SillyTavern 目录。", ToastKind.Warning);
            return;
        }
        var candidate = new AppSettings
        {
            InstallDirectory = path,
            StartServerOnLaunch = settings.StartServerOnLaunch,
            OpenBrowserOnAutostart = settings.OpenBrowserOnAutostart,
            AutoUpdateManager = settings.AutoUpdateManager,
            AutoRestartServer = settings.AutoRestartServer,
            Appearance = settings.Appearance
        };
        try { candidate.Save(); }
        catch (Exception ex) { ShowToast("无法保存设置：" + ex.Message, ToastKind.Error); return; }
        settings.InstallDirectory = candidate.InstallDirectory;
        configReadFailed = false;
        await RefreshEnvironmentAsync();
    }

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

    private ConfigurationState CurrentConfiguration()
    {
        if (server.Config is not null) return ConfigurationState.Configured;
        var directory = settings.InstallDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "server.js")))
            return ConfigurationState.Unconfigured;
        if (configReadFailed) return ConfigurationState.Error;
        return ConfigurationState.Pending;
    }

    private bool WatchdogBadgeActive => lastWatchdogRestartAt is { } at && DateTime.Now - at < TimeSpan.FromMinutes(30);

    private void ClearWatchdogBadge()
    {
        if (watchdogRestartCount == 0 && lastWatchdogRestartAt is null) return;
        watchdogRestartCount = 0;
        lastWatchdogRestartAt = null;
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

    private (int ProcessId, string Name)? QueryOccupier(int port)
    {
        if (DateTime.UtcNow - occupierQueriedUtc > TimeSpan.FromSeconds(5))
        {
            occupierQueriedUtc = DateTime.UtcNow;
            occupierInfo = ServerController.FindPortOwner(port);
        }
        return occupierInfo;
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
        SetTrayIcon(p.ToneColor(state.Tone));
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

    private async Task HandleActionAsync(ViewActionKind kind)
    {
        switch (kind)
        {
            case ViewActionKind.Stop: await StartOrStopAsync(); break;
            case ViewActionKind.Restart: await RestartAsync(); break;
            case ViewActionKind.OpenServer: OpenServer(); break;
            case ViewActionKind.OpenSettings: ShowSettings(); break;
            case ViewActionKind.ViewLog: RestoreWindow(); ResumeScroll(); break;
            case ViewActionKind.CopyDiagnostics: CopyDiagnostics(); break;
        }
    }

    private void PopulateAddresses()
    {
        var config = server.Config;
        if (config is null) { addressItems.Clear(); lastAddressConfig = null; return; }
        // Enumerating network adapters is a slow IP-helper call and UpdateDisplay runs on every
        // state change, so only re-enumerate for a new config or after a refresh interval.
        if (ReferenceEquals(config, lastAddressConfig) && DateTime.UtcNow - lastAddressRefreshUtc <= TimeSpan.FromSeconds(10)) return;
        lastAddressConfig = config;
        lastAddressRefreshUtc = DateTime.UtcNow;
        var selected = addressItems.Count > 0 && selectedAddress < addressItems.Count ? addressItems[selectedAddress].Url : null;
        var items = new List<AddressItem> { new("本机", config.LocalUrl) };
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.LocalUrl };
        if (config.Ipv4 || config.Ipv6)
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                             n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    try
                    {
                        var addresses = nic.GetIPProperties().UnicastAddresses;
                        if (config.Ipv4)
                        {
                            foreach (var address in addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                            {
                                if (IPAddress.IsLoopback(address.Address) ||
                                    address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                                var url = $"{(config.Ssl ? "https" : "http")}://{address.Address}:{config.Port}/";
                                if (urls.Add(url)) items.Add(new AddressItem(nic.Name, url));
                            }
                        }
                        if (config.Ipv6)
                        {
                            foreach (var address in addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6))
                            {
                                if (IPAddress.IsLoopback(address.Address) || address.Address.IsIPv6LinkLocal ||
                                    address.Address.IsIPv6Multicast) continue;
                                var url = $"{(config.Ssl ? "https" : "http")}://[{address.Address}]:{config.Port}/";
                                if (urls.Add(url)) items.Add(new AddressItem(nic.Name, url));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { }
        }
        if (addressItems.Select(x => x.Url).SequenceEqual(items.Select(x => x.Url))) return;
        addressItems.Clear();
        addressItems.AddRange(items);
        selectedAddress = Math.Max(0, items.FindIndex(x => x.Url == selected));
        UpdateAddressText();
    }

    private void UpdateAddressText()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0) addressPicker.SelectedText = "";
        else
        {
            selectedAddress = Math.Clamp(selectedAddress, 0, addressItems.Count - 1);
            var item = addressItems[selectedAddress];
            addressPicker.SelectedText = $"{item.Name} · {item.Url}";
        }
        addressPicker.Invalidate();
    }

    private void ShowAddressMenu()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0) return;
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            Font = Typography.Body,
            DropShadowEnabled = true
        };
        for (var i = 0; i < addressItems.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem($"{addressItems[i].Name} · {addressItems[i].Url}");
            item.Click += (_, _) => { selectedAddress = index; UpdateAddressText(); };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => Dwm.Apply(menu.Handle);
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(addressPicker, new Point(0, addressPicker.Height));
    }

    private sealed record AddressItem(string Name, string Url);

    private async void CopyAddress()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0 || selectedAddress >= addressItems.Count) return;
        try { Clipboard.SetText(addressItems[selectedAddress].Url); } catch (ExternalException) { return; }
        var original = copyAddressButton.IconGlyph;
        copyAddressButton.IconGlyph = Icons.Check;
        copyAddressButton.IconColorOverride = Theme.Current.SuccessText;
        copyAddressButton.Invalidate();
        await Task.Delay(1500);
        if (IsDisposed || copyAddressButton.IsDisposed) return;
        copyAddressButton.IconGlyph = original;
        copyAddressButton.IconColorOverride = null;
        copyAddressButton.Invalidate();
    }

    private async void CopyLog()
    {
        if (logView.SelectionLength > 0)
        {
            var value = SelectedLogText();
            if (string.IsNullOrEmpty(value)) return;
            try { Clipboard.SetText(value); } catch (ExternalException) { return; }
            await FlashCheck(copyLogButton);
            return;
        }
        // No selection: copy every currently visible line with full timestamps (v2 §4.7).
        var lines = renderedEntries.Where(entry => !entry.IsSeparator).Select(entry => entry.Source).ToList();        if (lines.Count == 0) return;
        try { Clipboard.SetText(string.Join("\r\n", lines)); } catch (ExternalException) { return; }
        ShowToast($"已复制 {lines.Count} 行日志（含完整时间戳）", ToastKind.Success);
        await FlashCheck(copyLogButton);
    }

    private async void CopyDiagnostics()
    {
        var config = server.Config;
        var state = server.State;
        string stateLine;
        if (state == ServerState.Running && runningSinceUtc is { } since)
            stateLine = $"运行中（已运行 {FormatDuration(DateTime.UtcNow - since)}）";
        else if (state == ServerState.Failed)
            stateLine = "服务异常退出" +
                (server.LastExitCode is { } code ? $"（退出码 {code}）" : "") +
                (lastRunDuration is { } duration ? $"，退出前已运行 {FormatDuration(duration)}" : "");
        else if (busyOperation != BusyOperation.None)
            stateLine = AppStateFactory.BusyTitle(busyOperation).TrimEnd('…');
        else stateLine = server.Detail;
        var security = config is null ? null : $"白名单{(config.Whitelist ? "开" : "关")} · 认证{(config.BasicAuth || config.UserAccounts ? "开" : "未开")}";
        string? extra = null;
        if (state == ServerState.Occupied && config is not null && QueryOccupier(config.Port) is { } owner)
            extra = $"占用进程：{owner.Name} (PID {owner.ProcessId})";
        var text = Diagnostics.Build(stateLine, config, branch, security, extra,
            settings.AutoRestartServer, watchdogRestartCount,
            lastWatchdogRestartAt?.ToString("HH:mm"), lastMaintenance, logLines);
        try { Clipboard.SetText(text); }
        catch (ExternalException) { ShowToast("复制失败，剪贴板被其他程序占用。", ToastKind.Warning); return; }
        ShowToast("已复制诊断信息", ToastKind.Success);
        await FlashCheck(copyDiagButton);
    }

    private static async Task FlashCheck(ThemedIconButton button)
    {
        var original = button.Glyph;
        button.Glyph = Icons.Check;
        button.Invalidate();
        await Task.Delay(1500);
        if (button.IsDisposed || button.Disposing) return;
        button.Glyph = original;
        button.Invalidate();
    }

    /// <summary>
    /// Maps the selected text back to the full source lines so copied text keeps
    /// complete timestamps even though the view collapses them. The view holds one
    /// hard line per rendered entry, so text lines align with renderedEntries by index.
    /// </summary>
    private string SelectedLogText()
    {
        var text = logView.Text;
        if (renderedEntries.Count == 0 || text.Length == 0) return logView.SelectedText;
        var selStart = Math.Clamp(logView.SelectionStart, 0, text.Length);
        var selEnd = Math.Clamp(logView.SelectionStart + logView.SelectionLength, 0, text.Length);
        var lines = text.Split('\n');
        var result = new List<string>();
        var offset = 0;
        for (var i = 0; i < lines.Length && offset <= selEnd; i++)
        {
            var lineEnd = offset + lines[i].Length;
            if (lineEnd >= selStart && i < renderedEntries.Count) result.Add(renderedEntries[i].Source);
            offset = lineEnd + 1;
        }
        return string.Join("\n", result);
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

    private void OpenSearchPopup()
    {
        if (searchPopup is { IsDisposed: false })
        {
            searchPopup.Box.FocusInput();
            return;
        }
        searchPopup = new SearchPopup { Box = { Text = logSearch.Text } };
        searchPopup.Box.TextChanged += (_, _) => { if (searchPopup is { IsDisposed: false }) logSearch.Text = searchPopup.Box.Text; };
        searchPopup.Box.NavigateRequested += forward => NavigateSearch(forward ? 1 : -1);
        var anchor = searchToggleButton.PointToScreen(new Point(0, searchToggleButton.Height + S(4)));
        var area = Screen.FromControl(this).WorkingArea;
        searchPopup.Location = new Point(
            Math.Min(Math.Max(area.Left + S(8), anchor.X - S(80)), area.Right - searchPopup.Width - S(8)),
            Math.Min(anchor.Y, area.Bottom - searchPopup.Height - S(8)));
        searchPopup.Show(this);
        searchPopup.Box.FocusInput();
    }

    private void NavigateSearch(int direction)
    {
        if (searchMatches.Count == 0) return;
        searchMatchIndex = ((searchMatchIndex + direction) % searchMatches.Count + searchMatches.Count) % searchMatches.Count;
        var (offset, length) = searchMatches[searchMatchIndex];
        logView.SelectionStart = offset;
        logView.SelectionLength = length;
        logView.ScrollToCaret();
        UpdateLogCount();
    }

    private void ApplySearchHighlights()
    {
        searchMatches.Clear();
        searchMatchIndex = -1;
        var query = logSearch.Text.Trim();
        if (query.Length == 0) return;
        var text = logView.Text;
        var index = 0;
        while ((index = text.IndexOf(query, index, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            searchMatches.Add((index, query.Length));
            index += query.Length;
        }
        if (searchMatches.Count == 0) return;
        foreach (var (offset, length) in searchMatches)
        {
            logView.SelectionStart = offset;
            logView.SelectionLength = length;
            logView.SelectionBackColor = Theme.Current.AccentSoft;
            logView.SelectionColor = Theme.Current.TextPrimary;
            logView.SelectionFont = Typography.MonoBold;
        }
        // Land on the newest match so it is on screen without scrolling.
        searchMatchIndex = searchMatches.Count - 1;
        var (activeOffset, activeLength) = searchMatches[searchMatchIndex];
        logView.SelectionStart = activeOffset;
        logView.SelectionLength = activeLength;
        logView.ScrollToCaret();
    }

    private void UpdateLogCount()
    {
        var query = logSearch.Text.Trim();
        if (query.Length > 0)
        {
            logCount.Visible = true;
            logCount.Text = searchMatches.Count == 0
                ? "0 处"
                : searchMatchIndex >= 0 ? $"{searchMatchIndex + 1} / {searchMatches.Count}" : $"{searchMatches.Count} 处";
            return;
        }
        var filtering = logSources.SelectedIndex > 0;
        logCount.Visible = filtering;
        if (filtering) logCount.Text = $"{logLines.Count(Matches)} 条";
    }

    private void OpenReadme()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 5 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, "README.md");
            if (File.Exists(candidate)) { OpenPath(candidate); return; }
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        OpenPath("https://github.com/wacilimonster-source/SillyTavernDesktop#readme");
    }

    /// <summary>Floating search input for the compact (600–679) layout (v2 §4.10).</summary>
    private sealed class SearchPopup : ThemedForm
    {
        public SearchPopup() : base(resizable: false)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(260, 42);
            Padding = new Padding(6);
            BackColor = Theme.Current.Surface;
            Box.Dock = DockStyle.Fill;
            Box.Margin = Fill;
            Controls.Add(Box);
            Deactivate += (_, _) => Close();
        }

        public SearchBox Box { get; } = new();
    }

    private void TogglePause()
    {
        if (pausedScroll) ResumeScroll(); else PauseScroll();
    }

    private void PauseScroll()
    {
        if (pausedScroll) return;
        pausedScroll = true;
        backToBottom.Visible = true;
        pauseButton.Glyph = Icons.Play;
        pauseButton.AccessibleName = "继续滚动";
        toolTip.SetToolTip(pauseButton, "继续滚动");
        PositionBackToBottom();
    }

    private void ResumeScroll()
    {
        pausedScroll = false;
        backToBottom.Visible = false;
        pauseButton.Glyph = Icons.Pause;
        pauseButton.AccessibleName = "暂停滚动";
        toolTip.SetToolTip(pauseButton, "暂停滚动");
        RebuildLog();
    }

    private void PositionBackToBottom()
    {
        if (logSurface is null) return;
        backToBottom.Location = new Point(
            Math.Max(S(8), logSurface.ClientSize.Width - backToBottom.Width - S(12)),
            Math.Max(S(8), logSurface.ClientSize.Height - backToBottom.Height - S(12)));
    }

    private void OpenServer() { if (server.Config is { } config) OpenPath(config.LocalUrl); else ShowToast("尚未读取服务地址。", ToastKind.Info); }

    private void OpenPath(string path, bool create = false)
    {
        try
        {
            if (create) Directory.CreateDirectory(path);
            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("路径不存在。", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast("无法打开：" + ex.Message, ToastKind.Warning); }
    }

    private void OnLogLine(string line)
    {
        queuedLines.Enqueue(line.Length > 8192 ? line[..8192] + "…（完整内容见日志文件）" : line);
        while (queuedLines.Count > 12000 && queuedLines.TryDequeue(out _)) Interlocked.Increment(ref omittedLiveLines);
    }

    private bool Matches(string line)
    {
        var source = logSources.SelectedIndex > 0 && logSources.SelectedIndex < logSources.Items.Length ? logSources.Items[logSources.SelectedIndex] : "全部";
        return (source == "全部" || line.Contains($"] [{source}] ", StringComparison.Ordinal))
            && (logSearch.Text.Length == 0 || line.Contains(logSearch.Text, StringComparison.CurrentCultureIgnoreCase));
    }

    private void FlushLogQueue()
    {
        var render = !hiddenToTray && !pausedScroll;
        var omitted = Interlocked.Exchange(ref omittedLiveLines, 0);
        if (omitted > 0)
        {
            var notice = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 实时窗口省略 {omitted} 条日志，完整记录请查看日志文件。";
            logLines.Add(notice);
            if (render && Matches(notice)) AppendColored(notice);
        }
        var appended = false;
        var matchedAppended = false;
        var searching = logSearch.Text.Length > 0;
        var budget = render ? Stopwatch.StartNew() : null;
        var limit = render ? 400 : 4000;
        var processed = 0;
        if (render) SuspendLogRedraw();
        try
        {
            while (processed < limit && (budget is null || budget.ElapsedMilliseconds < 40)
                   && queuedLines.TryDequeue(out var line))
            {
                logLines.Add(line);
                if (logLines.Count > 10000) logLines.RemoveRange(0, Math.Min(500, logLines.Count - 10000 + 499));
                if (render && Matches(line))
                {
                    AppendColored(line);
                    appended = true;
                    if (searching) matchedAppended = true;
                }
                processed++;
            }
        }
        finally { if (render) ResumeLogRedraw(); }
        if (!appended) return;
        // While searching, appended matches must be highlighted and counted; a rebuild is cheap enough.
        if (matchedAppended)
        {
            RebuildLog();
            return;
        }
        logView.SelectionStart = logView.TextLength;
        logView.SelectionLength = 0;
        logView.ScrollToCaret();
    }

    private void RebuildLog()
    {
        if (logView.IsDisposed) return;
        var visible = logLines.Where(Matches).ToArray();
        var truncated = visible.Length > 800;
        if (truncated) visible = visible[^800..];
        SuspendLogRedraw();
        logView.SuspendLayout();
        try
        {
            logView.Clear();
            renderedEntries.Clear();
            lastRenderedDate = null;
            var batch = visible.ToList();
            if (truncated)
                batch.Insert(0, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 仅显示最近 800 条匹配日志，完整记录见日志文件。");
            AppendColoredBatch(batch);
            if (!pausedScroll)
            {
                logView.SelectionStart = logView.TextLength;
                logView.SelectionLength = 0;
                logView.ScrollToCaret();
            }
            ApplySearchHighlights();
        }
        finally
        {
            logView.ResumeLayout();
            ResumeLogRedraw();
        }
        UpdateLogCount();
    }

    private static string DateSeparatorText(DateTime stamp) =>
        $"──────────── {stamp.Month} 月 {stamp.Day} 日 ────────────";

    private void AppendDateSeparatorIfNeeded(DateTime stamp)
    {
        var date = stamp.ToString("yyyy-MM-dd");
        if (date == lastRenderedDate) return;
        lastRenderedDate = date;
        var separator = DateSeparatorText(stamp);
        renderedEntries.Add((separator, true));
        logView.SelectionStart = logView.TextLength;
        logView.SelectionLength = 0;
        logView.SelectionColor = Theme.Current.TextTertiary;
        logView.AppendText(separator + Environment.NewLine);
    }

    private void AppendColored(string line)
    {
        var p = Theme.Current;
        var start = logView.TextLength;
        logView.SelectionStart = start;
        logView.SelectionLength = 0;
        if (!TryParseLogLine(line, out var stamp, out var time, out var source, out var message))
        {
            renderedEntries.Add((line, false));
            logView.SelectionColor = p.TextPrimary;
            logView.AppendText(line + Environment.NewLine);
            return;
        }
        AppendDateSeparatorIfNeeded(stamp);
        renderedEntries.Add((line, false));
        logView.SelectionColor = p.TextTertiary;
        logView.AppendText(time + "  ");
        logView.SelectionColor = source switch
        {
            "服务" => p.Accent,
            "维护" => p.Purple,
            _ => p.TextSecondary
        };
        logView.AppendText(source + "  ");
        logView.SelectionColor = p.TextPrimary;
        logView.AppendText(message + Environment.NewLine);
    }

    /// <summary>Inserts a whole rebuild batch with one native RTF call instead of per-line appends.</summary>
    private void AppendColoredBatch(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var p = Theme.Current;
        var fontSize = Math.Max(1, (int)Math.Round(Typography.Mono.SizeInPoints * 2));
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 ").Append(Typography.Mono.FontFamily.Name).Append(@";}}");
        rtf.Append(@"{\colortbl ;");
        AppendRtfColor(rtf, p.TextTertiary);
        AppendRtfColor(rtf, p.Accent);
        AppendRtfColor(rtf, p.Purple);
        AppendRtfColor(rtf, p.TextSecondary);
        AppendRtfColor(rtf, p.TextPrimary);
        rtf.Append(@"}\viewkind4\uc1\pard\f0\fs").Append(fontSize.ToString(CultureInfo.InvariantCulture)).Append(' ');
        foreach (var line in lines)
        {
            if (!TryParseLogLine(line, out var stamp, out var time, out var source, out var message))
            {
                renderedEntries.Add((line, false));
                rtf.Append(@"\cf5 ");
                AppendRtfText(rtf, line);
                rtf.Append(@"\par").Append('\n');
                continue;
            }
            var date = stamp.ToString("yyyy-MM-dd");
            if (date != lastRenderedDate)
            {
                lastRenderedDate = date;
                var separator = DateSeparatorText(stamp);
                renderedEntries.Add((separator, true));
                rtf.Append(@"\cf1 ");
                AppendRtfText(rtf, separator);
                rtf.Append(@"\par").Append('\n');
            }
            renderedEntries.Add((line, false));
            rtf.Append(@"\cf1 ");
            AppendRtfText(rtf, time + "  ");
            rtf.Append(@"\cf").Append(source switch { "服务" => '2', "维护" => '3', _ => '4' }).Append(' ');
            AppendRtfText(rtf, source + "  ");
            rtf.Append(@"\cf5 ");
            AppendRtfText(rtf, message);
            rtf.Append(@"\par").Append('\n');
        }
        rtf.Append('}');
        logView.SelectedRtf = rtf.ToString();
    }

    private static void AppendRtfColor(StringBuilder builder, Color color) =>
        builder.Append(@"\red").Append(color.R.ToString(CultureInfo.InvariantCulture))
            .Append(@"\green").Append(color.G.ToString(CultureInfo.InvariantCulture))
            .Append(@"\blue").Append(color.B.ToString(CultureInfo.InvariantCulture)).Append(';');

    private static void AppendRtfText(StringBuilder builder, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\r': break;
                case '\n': builder.Append(@"\par").Append('\n'); break;
                default:
                    if (ch < 128) builder.Append(ch);
                    else builder.Append(@"\u").Append(((short)ch).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
    }

    private static bool TryParseLogLine(string line, out DateTime stamp, out string time, out string source, out string message)
    {
        stamp = DateTime.MinValue;
        time = source = message = "";
        if (line.Length < 22 || line[0] != '[') return false;
        var close = line.IndexOf(']');
        if (close < 0) return false;
        if (!DateTime.TryParseExact(line[1..close], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return false;
        var rest = line[(close + 1)..].TrimStart();
        if (rest.Length < 3 || rest[0] != '[') return false;
        var close2 = rest.IndexOf(']');
        if (close2 < 0) return false;
        // The date itself comes from the separator rows; lines only carry the clock time.
        stamp = timestamp;
        time = timestamp.ToString("HH:mm:ss");
        source = rest[1..close2];
        message = rest[(close2 + 1)..].TrimStart();
        return true;
    }

    private void PositionAtBottomRight()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - S(12)), Math.Max(area.Top, area.Bottom - Height - S(12)));
    }

    private bool firstTrayHide = true;

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

    private void SetTrayIcon(Color color, bool force = false)
    {
        if (!force && color == lastTrayColor && activeTrayIcon is not null) return;
        lastTrayColor = color;
        var size = SystemInformation.SmallIconSize;
        var width = Math.Max(16, size.Width);
        var height = Math.Max(16, size.Height);
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var baseIcon = new Icon(Icon ?? SystemIcons.Application, width, height);
            graphics.DrawIcon(baseIcon, new Rectangle(0, 0, width, height));
        }
        catch { graphics.DrawIcon(SystemIcons.Application, new Rectangle(0, 0, width, height)); }
        var dot = Math.Max(5, (int)(width * 0.42f));
        var ring = Math.Max(1, width / 12);
        var x = width - dot - ring * 2 - 1;
        var y = height - dot - ring * 2 - 1;
        using (var outline = new SolidBrush(Color.White))
            graphics.FillEllipse(outline, x, y, dot + ring * 2, dot + ring * 2);
        using (var brush = new SolidBrush(color))
            graphics.FillEllipse(brush, x + ring, y + ring, dot, dot);
        var handle = bitmap.GetHicon();
        try
        {
            using var transient = Icon.FromHandle(handle);
            var next = (Icon)transient.Clone();
            tray.Icon = next;
            activeTrayIcon?.Dispose();
            activeTrayIcon = next;
        }
        finally { DestroyIcon(handle); }
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

    private void SuspendLogRedraw()
    {
        if (logView.IsHandleCreated) SendMessage(logView.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
    }

    private void ResumeLogRedraw()
    {
        if (!logView.IsHandleCreated) return;
        SendMessage(logView.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
        logView.Invalidate();
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
