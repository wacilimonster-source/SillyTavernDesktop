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

// UI construction and the tray icon: every control the window is built from lives here,
// so the other partial files can focus on behaviour instead of layout plumbing.
internal sealed partial class MainForm
{
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
    private Icon? activeTrayIcon;
    private Color lastTrayColor = Color.Empty;

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

    private readonly StepProgress stepProgress = new();
    private readonly FlowLayoutPanel cancelFlow = new();
    private readonly ThemedButton cancelMaintenanceButton = new();
    private readonly ThemedLabel cancelHintLabel = new() { Tone = LabelTone.Tertiary, AutoSize = true };
    private readonly ThemedLabel elapsedLabel = new() { Tone = LabelTone.Tertiary, AutoSize = true };
    private readonly ThemedLabel watchdogBadge = new() { Tone = LabelTone.Success, AutoSize = true, Visible = false, BackColor = Color.Transparent };

    private readonly RoundedPanel onboarding;
    private RowStyle? logRow;
    private RowStyle? onboardingRow;

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
}
