using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
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
    private ThemedIconButton openLogButton = null!;
    private readonly LogRichTextBox logView = new();
    private ThemedButton backToBottom = null!;
    private RoundedPanel logSurface = null!;

    private readonly RoundedPanel onboarding;
    private RowStyle? logRow;
    private RowStyle? onboardingRow;

    private readonly ToastHost toast = new();

    private readonly ConcurrentQueue<string> queuedLines = new();
    private readonly List<string> logLines = new();
    private readonly System.Windows.Forms.Timer logTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer managerUpdateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly CancellationTokenSource managerUpdateLifetime = new();
    private CancellationTokenSource autoUpdateCheck = new();
    private ManagerUpdateService? managerUpdates;
    private string? lastUpdateNotification;
    private int omittedLiveLines;
    private Icon? activeTrayIcon;
    private Color lastTrayColor = Color.Empty;
    private BusyOperation busyOperation;
    private bool reallyClosing, stopSignalThread, pausedScroll, bannerDismissed, configReadFailed;
    private string branch = "未知";
    private PrimaryActionKind primaryKind;

    static MainForm() { Theme.Initialize(); }

    public MainForm(bool autoStarted, EventWaitHandle showSignal)
    {
        this.autoStarted = autoStarted;
        this.showSignal = showSignal;
        Theme.Initialize();
        settings = AppSettings.Load();
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
        ClientSize = new Size((int)Math.Round(720 * scale), (int)Math.Round(560 * scale));
        MinimumSize = new Size((int)Math.Round(600 * scale), (int)Math.Round(480 * scale));
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
        bannerAction.Click += async (_, _) => await ExitAsync();
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

        logLines.AddRange(log.ReadRecent());
        log.LineWritten += OnLogLine;
        server.Changed += OnServerChanged;
        logTimer.Tick += (_, _) => FlushLogQueue();
        logTimer.Start();
        managerUpdateTimer.Tick += async (_, _) => await CheckManagerUpdateAsync(manual: false);
        RebuildLog();
        UpdateDisplay();

        Shown += async (_, _) =>
        {
            PositionAtBottomRight();
            PositionToast();
            if (autoStarted) { Hide(); Opacity = 1; }
            await InitializeAsync();
            if (settings.AutoUpdateManager)
            {
                managerUpdateTimer.Start();
                try { await Task.Delay(TimeSpan.FromSeconds(5), managerUpdateLifetime.Token); await CheckManagerUpdateAsync(manual: false); }
                catch (OperationCanceledException) { }
            }
        };
        Resize += (_, _) => { PositionToast(); if (WindowState == FormWindowState.Minimized) HideToTray(); };
        _ = Task.Run(() =>
        {
            while (!stopSignalThread)
            {
                showSignal.WaitOne();
                if (stopSignalThread) break;
                TryUi(RestoreWindow);
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

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(30)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(20)));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(34)));

        var head = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Fill };
        statusDot = new StatusDot { Margin = new Padding(0, S(5), S(8), 0), BackColor = Color.Transparent };
        statusLabel = new ThemedLabel { Font = Typography.Status, Tone = LabelTone.Primary, Text = "已停止", AutoSize = true, Margin = Fill };
        head.Controls.Add(statusDot);
        head.Controls.Add(statusLabel);

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
        progressFlow.Anchor = AnchorStyles.Right;
        progressFlow.Visible = false;
        spinner.Margin = new Padding(0, S(8), S(8), 0);
        progressLabel.AutoSize = true;
        progressLabel.Margin = new Padding(0, S(7), 0, 0);
        progressFlow.Controls.Add(spinner);
        progressFlow.Controls.Add(progressLabel);

        grid.Controls.Add(head, 0, 0);
        grid.Controls.Add(metaLabel, 0, 1);
        grid.Controls.Add(statusActions, 0, 2);
        grid.Controls.Add(primaryButton, 1, 0);
        grid.SetRowSpan(primaryButton, 3);
        grid.Controls.Add(progressFlow, 1, 0);
        grid.SetRowSpan(progressFlow, 3);
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
            Text = "复制地址",
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
        pauseButton = LogIcon(Icons.Pause, "暂停滚动", (_, _) => TogglePause());
        logSearch.Width = S(140);
        logSearch.Margin = new Padding(0, S(2), S(4), 0);
        logSources.AccessibleName = "日志来源";
        logSources.Items = ["全部", "服务", "维护", "管理器"];
        logSources.SelectedIndex = 0;
        logSources.Width = logSources.MeasureWidth();
        logSources.Margin = new Padding(0, S(3), S(4), 0);
        logSources.SelectedIndexChanged += (_, _) => RebuildLog();
        logSearch.TextChanged += (_, _) => RebuildLog();
        tools.Controls.Add(openLogButton);
        tools.Controls.Add(copyLogButton);
        tools.Controls.Add(pauseButton);
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
        var openSettings = new ThemedButton { Text = "打开设置", Variant = ButtonVariant.Quiet, IconGlyph = Icons.Settings, PreferredHeight = 34 };
        openSettings.Click += (_, _) => ShowSettings();
        actions.Controls.Add(browse);
        actions.Controls.Add(openSettings);
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
        AddTrayItem(trayUpdate, Icons.Restart, async (_, _) => await CheckUpdatesAsync());
        AddTrayItem(trayManagerUpdate, Icons.Download, async (_, _) => await CheckManagerUpdateAsync(manual: true));
        AddTrayItem(trayRepair, Icons.Repair, async (_, _) => await RepairAsync());
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(traySettings, Icons.Settings, (_, _) => ShowSettings());
        trayMenu.Items.Add(new ToolStripSeparator());
        AddTrayItem(trayExit, Icons.Power, async (_, _) => await ExitAsync(), danger: true);
    }

    private void AddTrayItem(ToolStripMenuItem item, string glyph, EventHandler handler, bool danger = false, bool bold = false)
    {
        item.AutoSize = false;
        item.Width = S(248);
        item.Height = S(32);
        item.Padding = Padding.Empty;
        item.Font = bold ? Typography.ButtonBold : Typography.Body;
        item.Tag = danger ? "danger" : null;
        item.Image = Icons.ToBitmap(glyph, S(16), danger ? Theme.Current.DangerText : Theme.Current.TextSecondary);
        item.ImageScaling = ToolStripItemImageScaling.None;
        item.Click += handler;
        trayGlyphs[item] = glyph;
        trayMenu.Items.Add(item);
    }

    private async Task InitializeAsync()
    {
        var pending = PendingManagerUpdateStore.Read();
        if (pending is { Failed: true })
        {
            log.Write("管理器更新", "上次自动安装失败，可从托盘手动检查管理器更新后重试。");
            Notify("管理器更新未安装", "请查看日志，或从托盘手动检查管理器更新。");
        }
        await RunExclusiveAsync(BusyOperation.ReadingConfig, async () =>
        {
            if (settings.StartServerOnLaunch) await server.StartAsync(autoStarted);
            else await server.RefreshConfigAsync();
            branch = await maintenance.GetBranchAsync();
        }, showErrors: !autoStarted);
        UpdateDisplay();
    }

    private async Task RunExclusiveAsync(BusyOperation operation, Func<Task> action, bool showErrors = true)
    {
        if (busyOperation != BusyOperation.None || reallyClosing) return;
        busyOperation = operation;
        UpdateDisplay();
        try
        {
            await action();
            if (operation == BusyOperation.ReadingConfig) configReadFailed = false;
        }
        catch (OperationCanceledException ex)
        {
            log.Write("管理器", ex.Message);
            if (showErrors) ShowToast(ex.Message, ToastKind.Info);
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
            if (!IsDisposed) UpdateDisplay();
        }
    }

    private async Task StartOrStopAsync() => await RunExclusiveAsync(
        server.HasOwnedProcess ? BusyOperation.Stopping : BusyOperation.Starting,
        async () =>
        {
            if (server.HasOwnedProcess) await server.StopAsync(AskStopDecisionAsync);
            else { await server.StartAsync(); branch = await maintenance.GetBranchAsync(); }
        });

    private async Task RestartAsync()
    {
        if (server.HasOwnedProcess)
        {
            var stopped = false;
            await RunExclusiveAsync(BusyOperation.Stopping, async () => { stopped = await server.StopAsync(AskStopDecisionAsync); });
            if (!stopped) return;
        }
        await RunExclusiveAsync(BusyOperation.Starting, async () =>
        {
            await server.StartAsync();
            branch = await maintenance.GetBranchAsync();
        });
    }

    private async Task CheckUpdatesAsync() => await RunExclusiveAsync(BusyOperation.CheckingUpdates, async () =>
    {
        var result = await maintenance.CheckUpdatesAsync();
        ShowToast(result, ToastKind.Info);
    });

    private async Task RefreshEnvironmentAsync() => await RunExclusiveAsync(BusyOperation.ReadingConfig, async () =>
    {
        await server.RefreshConfigAsync();
        branch = await maintenance.GetBranchAsync();
    });

    private async Task RunPrimaryAsync()
    {
        switch (primaryKind)
        {
            case PrimaryActionKind.OpenServer: OpenServer(); break;
            case PrimaryActionKind.Start: await StartOrStopAsync(); break;
            case PrimaryActionKind.Retry: await RefreshEnvironmentAsync(); break;
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
            AutoUpdateManager = settings.AutoUpdateManager
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
            "更新会停止服务并可能中断正在进行的聊天。\n将使用官方 Git 和 npm 命令。", "开始更新")) return;
        await RunExclusiveAsync(BusyOperation.Updating, async () =>
        {
            await maintenance.UpdateAsync(AskStopDecisionAsync);
            branch = await maintenance.GetBranchAsync();
        });
    }

    private async Task RepairAsync()
    {
        if (!ConfirmDialog.AskConfirm(this, "安装依赖",
            "依赖修复会停止服务，并运行官方 npm install 命令。", "开始修复")) return;
        await RunExclusiveAsync(BusyOperation.Repairing, () => maintenance.RepairAsync(AskStopDecisionAsync));
    }

    private Task<StopDecision> AskStopDecisionAsync()
    {
        var choice = ConfirmDialog.AskStop(this, "SillyTavern 没有在限定时间内退出。\n强制结束可能中断尚未完成的操作。");
        return Task.FromResult(choice switch
        {
            StopChoice.Wait => StopDecision.Wait,
            StopChoice.Force => StopDecision.Force,
            _ => StopDecision.Cancel
        });
    }

    private async Task ExitAsync()
    {
        if (busyOperation != BusyOperation.None)
        {
            ShowToast("请等待当前操作结束后再退出。", ToastKind.Info);
            return;
        }
        var canClose = false;
        await RunExclusiveAsync(BusyOperation.Exiting, async () =>
        {
            canClose = !server.HasOwnedProcess || await server.StopAsync(AskStopDecisionAsync);
        });
        if (!canClose) return;
        managerUpdateLifetime.Cancel();
        var updateReady = managerUpdates is null || await managerUpdates.PrepareForExitAsync();
        if (updateReady) ManagerUpdateInstaller.TryLaunch(restart: false, autostart: false);
        reallyClosing = true;
        Close();
    }

    private void ShowSettings()
    {
        if (busyOperation != BusyOperation.None) return;
        using var dialog = new SettingsForm(settings, server.HasOwnedProcess);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (settings.AutoUpdateManager) { managerUpdateTimer.Start(); _ = CheckManagerUpdateAsync(manual: false); }
        else
        {
            managerUpdateTimer.Stop();
            autoUpdateCheck.Cancel();
            autoUpdateCheck.Dispose();
            autoUpdateCheck = new CancellationTokenSource();
        }
        _ = RefreshEnvironmentAsync();
    }

    private void OnServerChanged()
    {
        TryUi(() =>
        {
            if (server.State == ServerState.Failed) Notify("SillyTavern 已退出", server.Detail);
            UpdateDisplay();
        });
    }

    private ConfigurationState CurrentConfiguration()
    {
        if (server.Config is not null) return ConfigurationState.Configured;
        if (configReadFailed) return ConfigurationState.Error;
        var directory = settings.InstallDirectory;
        if (!string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "server.js")))
            return ConfigurationState.Pending;
        return ConfigurationState.Unconfigured;
    }

    private void UpdateDisplay()
    {
        if (IsDisposed) return;
        var p = Theme.Current;
        var configuration = CurrentConfiguration();
        var config = server.Config;
        var metadata = config is not null
            ? $"管理器 {ManagerVersion.Current} · ST {config.Version} · {branch} · 端口 {config.Port} · 白名单{(config.Whitelist ? "开" : "关")} · 认证{(config.BasicAuth || config.UserAccounts ? "开" : "未开")}"
            : $"管理器 {ManagerVersion.Current} · 等待读取 ST 配置";
        var state = AppStateFactory.Compute(server.State, server.HasOwnedProcess, busyOperation, configuration, server.Detail, metadata);

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

        primaryKind = state.PrimaryKind;
        primaryButton.Visible = !state.ShowProgress && state.PrimaryKind != PrimaryActionKind.None;
        if (primaryButton.Visible)
        {
            primaryButton.Text = state.PrimaryLabel;
            primaryButton.IconGlyph = state.PrimaryIcon;
            primaryButton.AccessibleName = state.PrimaryLabel;
            primaryButton.Size = primaryButton.GetPreferredSize(Size.Empty);
        }
        progressFlow.Visible = state.ShowProgress;
        progressLabel.Text = state.ProgressLabel;

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

        var pending = PendingManagerUpdateStore.Read();
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
        PositionBackToBottom();
    }

    private void RebuildStatusActions(AppViewState state, Palette p)
    {
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
        }
    }

    private void PopulateAddresses()
    {
        var config = server.Config;
        if (config is null) { addressItems.Clear(); return; }
        var selected = addressItems.Count > 0 && selectedAddress < addressItems.Count ? addressItems[selectedAddress].Url : null;
        var items = new List<AddressItem> { new("本机", config.LocalUrl) };
        if (config.Ipv4)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    if (address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    items.Add(new AddressItem(nic.Name, $"{(config.Ssl ? "https" : "http")}://{address.Address}:{config.Port}/"));
                }
            }
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
        var value = logView.SelectionLength > 0 ? logView.SelectedText : logView.Text;
        if (string.IsNullOrEmpty(value)) return;
        try { Clipboard.SetText(value); } catch (ExternalException) { return; }
        var original = copyLogButton.Glyph;
        copyLogButton.Glyph = Icons.Check;
        copyLogButton.Invalidate();
        await Task.Delay(1500);
        if (IsDisposed || copyLogButton.IsDisposed) return;
        copyLogButton.Glyph = original;
        copyLogButton.Invalidate();
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
        logView.SelectionStart = logView.TextLength;
        logView.SelectionLength = 0;
        logView.ScrollToCaret();
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
        var omitted = Interlocked.Exchange(ref omittedLiveLines, 0);
        if (omitted > 0)
        {
            var notice = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 实时窗口省略 {omitted} 条日志，完整记录请查看日志文件。";
            logLines.Add(notice);
            if (Matches(notice)) AppendColored(notice);
        }
        var oldSelection = logView.SelectionStart;
        var oldScroll = Point.Empty;
        if (pausedScroll) SendMessage(logView.Handle, 0x04DD, IntPtr.Zero, ref oldScroll);
        var appended = false;
        for (var i = 0; i < 400 && queuedLines.TryDequeue(out var line); i++)
        {
            logLines.Add(line);
            if (logLines.Count > 10000) logLines.RemoveRange(0, Math.Min(500, logLines.Count - 10000 + 499));
            if (Matches(line)) { AppendColored(line); appended = true; }
        }
        if (!appended) return;
        if (!pausedScroll)
        {
            logView.SelectionStart = logView.TextLength;
            logView.SelectionLength = 0;
            logView.ScrollToCaret();
        }
        else
        {
            logView.SelectionStart = Math.Min(oldSelection, logView.TextLength);
            SendMessage(logView.Handle, 0x04DE, IntPtr.Zero, ref oldScroll);
        }
    }

    private void RebuildLog()
    {
        if (logView.IsDisposed) return;
        var visible = logLines.Where(Matches).ToArray();
        var truncated = visible.Length > 800;
        if (truncated) visible = visible[^800..];
        logView.SuspendLayout();
        logView.Clear();
        if (truncated)
            AppendColored($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 仅显示最近 800 条匹配日志，完整记录见日志文件。");
        foreach (var line in visible) AppendColored(line);
        logView.ResumeLayout();
        if (!pausedScroll)
        {
            logView.SelectionStart = logView.TextLength;
            logView.SelectionLength = 0;
            logView.ScrollToCaret();
        }
        var filtering = logSearch.Text.Length > 0 || logSources.SelectedIndex > 0;
        logCount.Visible = filtering;
        if (filtering) logCount.Text = $"{logLines.Count(Matches)} 条";
    }

    private void AppendColored(string line)
    {
        var p = Theme.Current;
        var start = logView.TextLength;
        logView.SelectionStart = start;
        logView.SelectionLength = 0;
        if (!TryParseLogLine(line, out var time, out var source, out var message))
        {
            logView.SelectionColor = p.TextPrimary;
            logView.AppendText(line + Environment.NewLine);
            return;
        }
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

    private static bool TryParseLogLine(string line, out string time, out string source, out string message)
    {
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
        time = timestamp.Date == DateTime.Today ? timestamp.ToString("HH:mm:ss") : timestamp.ToString("MM-dd HH:mm:ss");
        source = rest[1..close2];
        message = rest[(close2 + 1)..].TrimStart();
        return true;
    }

    private void PositionAtBottomRight()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - S(12)), Math.Max(area.Top, area.Bottom - Height - S(12)));
    }

    private void HideToTray() { Hide(); WindowState = FormWindowState.Normal; }

    private void RestoreWindow()
    {
        if (IsDisposed) return;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        PositionToast();
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
        if (keyData == (Keys.Control | Keys.F)) { logSearch.FocusInput(); return true; }
        if (keyData == (Keys.Control | Keys.End)) { ResumeScroll(); return true; }
        if (keyData == Keys.F5) { _ = RefreshEnvironmentAsync(); return true; }
        if (keyData == (Keys.Control | Keys.Oemcomma)) { ShowSettings(); return true; }
        if (keyData == Keys.Enter)
        {
            var focused = Control.FromHandle(GetFocus());
            if (focused is ThemedButton or ThemedIconButton or AddressPicker or TextBox or RichTextBox)
                return base.ProcessCmdKey(ref msg, keyData);
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!reallyClosing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideToTray(); return; }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            stopSignalThread = true;
            try { showSignal.Set(); } catch (ObjectDisposedException) { }
            logTimer.Stop(); logTimer.Dispose();
            managerUpdateTimer.Stop(); managerUpdateTimer.Dispose();
            managerUpdateLifetime.Cancel(); autoUpdateCheck.Cancel(); managerUpdates?.Dispose();
            log.LineWritten -= OnLogLine; server.Changed -= OnServerChanged;
            tray.Visible = false; tray.Dispose(); activeTrayIcon?.Dispose(); trayMenu.Dispose(); toolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
