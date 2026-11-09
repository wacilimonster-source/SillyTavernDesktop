using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace SillyTavernTray;

internal sealed class MainForm : Form
{
    private readonly bool autoStarted;
    private readonly EventWaitHandle showSignal;
    private readonly AppSettings settings;
    private readonly AppLog log = new();
    private readonly ServerController server;
    private readonly MaintenanceService maintenance;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem trayStartStop = new("启动服务");
    private readonly ToolStripMenuItem trayRestart = new("重启服务");
    private readonly ToolStripMenuItem trayUpdate = new("检查更新");
    private readonly ToolStripMenuItem trayManagerUpdate = new("检查管理器更新");
    private readonly Label status = new() { AutoSize = true, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold) };
    private readonly Label metadata = new() { AutoSize = true, ForeColor = Color.FromArgb(105, 113, 124) };
    private readonly Button startStop = new();
    private readonly Button restart = new();
    private readonly Button open = new();
    private readonly Button update = new();
    private readonly Button checkUpdate = new();
    private readonly Button repair = new();
    private readonly Button settingsButton = new();
    private readonly ComboBox addresses = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox sources = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 74 };
    private readonly TextBox search = new() { PlaceholderText = "搜索日志", Width = 150 };
    private readonly CheckBox pause = new() { Text = "暂停滚动", AutoSize = true, Margin = new Padding(5, 6, 0, 0) };
    private readonly RichTextBox logView = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(250, 251, 253), ForeColor = Color.FromArgb(40, 46, 55),
        Font = new Font("Consolas", 9), WordWrap = false, DetectUrls = false
    };
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
    private bool busy, reallyClosing, stopSignalThread;
    private string busyLabel = "";
    private string branch = "未知";

    public MainForm(bool autoStarted, EventWaitHandle showSignal)
    {
        this.autoStarted = autoStarted;
        this.showSignal = showSignal;
        settings = AppSettings.Load();
        server = new ServerController(settings, log);
        maintenance = new MaintenanceService(settings, log, server);
        Text = "SillyTavern 管理器";
        Font = new Font("Microsoft YaHei UI", 9);
        BackColor = Color.White;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(590, 450);
        StartPosition = FormStartPosition.Manual;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? (Icon)SystemIcons.Application.Clone();
        PositionAtBottomRight();
        if (autoStarted) Opacity = 0;
        BuildLayout();

        trayMenu.Items.Add("打开管理窗口", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("打开 SillyTavern", null, (_, _) => OpenServer());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayStartStop);
        trayMenu.Items.Add(trayRestart);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("查看日志", null, (_, _) => RestoreWindow());
        trayUpdate.Text = "检查 SillyTavern 更新";
        trayMenu.Items.Add(trayUpdate);
        trayMenu.Items.Add(trayManagerUpdate);
        trayMenu.Items.Add("设置", null, (_, _) => ShowSettings());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出并停止服务", null, async (_, _) => await ExitAsync());
        trayStartStop.Click += async (_, _) => await StartOrStopAsync();
        trayRestart.Click += async (_, _) => await RestartAsync();
        trayUpdate.Click += async (_, _) => await CheckUpdatesAsync();
        trayManagerUpdate.Click += async (_, _) => await CheckManagerUpdateAsync(manual: true);
        tray = new NotifyIcon { Visible = true, ContextMenuStrip = trayMenu, Text = "SillyTavern 管理器" };
        tray.DoubleClick += (_, _) => RestoreWindow();
        SetTrayIcon(Color.FromArgb(140, 146, 154));

        logLines.AddRange(log.ReadRecent());
        log.LineWritten += OnLogLine;
        server.Changed += OnServerChanged;
        sources.Items.AddRange(["全部", "服务", "维护", "管理器"]);
        sources.SelectedIndex = 0;
        sources.SelectedIndexChanged += (_, _) => RebuildLog();
        search.TextChanged += (_, _) => RebuildLog();
        logTimer.Tick += (_, _) => FlushLogQueue();
        logTimer.Start();
        managerUpdateTimer.Tick += async (_, _) => await CheckManagerUpdateAsync(manual: false);
        RebuildLog();
        UpdateDisplay();

        Shown += async (_, _) =>
        {
            PositionAtBottomRight();
            if (autoStarted) { Hide(); Opacity = 1; }
            await InitializeAsync();
            if (settings.AutoUpdateManager)
            {
                managerUpdateTimer.Start();
                try { await Task.Delay(TimeSpan.FromSeconds(5), managerUpdateLifetime.Token); await CheckManagerUpdateAsync(manual: false); }
                catch (OperationCanceledException) { }
            }
        };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };
        _ = Handle;
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

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10), ColumnCount = 1, RowCount = 7 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        Controls.Add(root);

        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(status);
        heading.Controls.Add(metadata);
        root.Controls.Add(heading, 0, 0);

        var actions = Row();
        ConfigureButton(startStop, "启动服务", 96, async (_, _) => await StartOrStopAsync(), primary: true);
        ConfigureButton(restart, "重启", 72, async (_, _) => await RestartAsync());
        ConfigureButton(open, "打开 SillyTavern", 135, (_, _) => OpenServer());
        actions.Controls.AddRange([startStop, restart, open]);
        root.Controls.Add(actions, 0, 1);

        var addressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 2, 0, 4) };
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        addressRow.Controls.Add(new Label { Text = "地址", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        addressRow.Controls.Add(addresses, 1, 0);
        var copyAddress = new Button { Text = "复制", Dock = DockStyle.Fill, Margin = new Padding(5, 0, 0, 0), FlatStyle = FlatStyle.Flat };
        copyAddress.Click += (_, _) => CopyAddress();
        addressRow.Controls.Add(copyAddress, 2, 0);
        root.Controls.Add(addressRow, 0, 2);

        var tools = Row();
        ConfigureButton(checkUpdate, "检查 ST 更新", 100, async (_, _) => await CheckUpdatesAsync());
        ConfigureButton(update, "更新 ST", 75, async (_, _) => await UpdateAsync());
        ConfigureButton(repair, "依赖修复", 88, async (_, _) => await RepairAsync());
        ConfigureButton(settingsButton, "设置", 66, (_, _) => ShowSettings());
        tools.Controls.AddRange([checkUpdate, update, repair, settingsButton]);
        root.Controls.Add(tools, 0, 3);

        var logTools = Row();
        logTools.Controls.Add(new Label { Text = "日志", AutoSize = true, Margin = new Padding(0, 6, 5, 0) });
        logTools.Controls.Add(sources);
        logTools.Controls.Add(search);
        logTools.Controls.Add(pause);
        var copyLog = new Button { Text = "复制", Width = 54, Height = 26, FlatStyle = FlatStyle.Flat, Margin = new Padding(5, 1, 0, 0) };
        copyLog.Click += (_, _) =>
        {
            var value = logView.SelectionLength > 0 ? logView.SelectedText : logView.Text;
            if (!string.IsNullOrEmpty(value)) try { Clipboard.SetText(value); } catch (System.Runtime.InteropServices.ExternalException) { }
        };
        logTools.Controls.Add(copyLog);
        root.Controls.Add(logTools, 0, 4);
        root.Controls.Add(logView, 0, 5);

        var footer = Row();
        var logFolder = Link("日志目录", (_, _) => OpenPath(log.DirectoryPath, create: true));
        var config = Link("打开配置", (_, _) => OpenPath(Path.Combine(settings.InstallDirectory, "config.yaml")));
        var data = Link("数据目录", (_, _) => OpenPath(server.Config?.DataRoot ?? Path.Combine(settings.InstallDirectory, "data")));
        var exit = Link("退出程序", async (_, _) => await ExitAsync());
        footer.Controls.AddRange([logFolder, config, data, exit]);
        root.Controls.Add(footer, 0, 6);
    }

    private static FlowLayoutPanel Row() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
    private static LinkLabel Link(string text, EventHandler handler)
    {
        var result = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 10, 18, 0), LinkColor = Color.FromArgb(58, 100, 176) };
        result.Click += handler;
        return result;
    }
    private static void ConfigureButton(Button button, string text, int width, EventHandler handler, bool primary = false)
    {
        button.Text = text; button.Width = width; button.Height = 30; button.Margin = new Padding(0, 2, 7, 0);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? Color.FromArgb(62, 101, 176) : Color.FromArgb(215, 220, 228);
        button.BackColor = primary ? Color.FromArgb(62, 101, 176) : Color.FromArgb(248, 249, 251);
        button.ForeColor = primary ? Color.White : Color.FromArgb(38, 45, 54);
        button.Click += handler;
    }

    private async Task InitializeAsync()
    {
        var pending = PendingManagerUpdateStore.Read();
        if (pending is { Failed: true })
        {
            log.Write("管理器更新", "上次自动安装失败，可从托盘手动检查管理器更新后重试。");
            tray.BalloonTipTitle = "管理器更新未安装";
            tray.BalloonTipText = "请查看日志，或从托盘手动检查管理器更新。";
            tray.ShowBalloonTip(5000);
        }
        await RunExclusiveAsync("正在检查环境…", async () =>
        {
            if (settings.StartServerOnLaunch) await server.StartAsync(autoStarted);
            else await server.RefreshConfigAsync();
            branch = await maintenance.GetBranchAsync();
        }, showErrors: !autoStarted);
    }

    private async Task RunExclusiveAsync(string label, Func<Task> action, bool showErrors = true)
    {
        if (busy || reallyClosing) return;
        busy = true; busyLabel = label; UpdateDisplay();
        try { await action(); }
        catch (OperationCanceledException ex) { log.Write("管理器", ex.Message); if (showErrors) ShowMessage(ex.Message, MessageBoxIcon.Information); }
        catch (Exception ex)
        {
            log.Write("管理器", ex.Message);
            if (showErrors) ShowMessage(ex.Message, MessageBoxIcon.Warning);
            else { tray.BalloonTipTitle = "SillyTavern 启动失败"; tray.BalloonTipText = ex.Message; tray.ShowBalloonTip(5000); }
        }
        finally { busy = false; busyLabel = ""; if (!IsDisposed) UpdateDisplay(); }
    }

    private async Task StartOrStopAsync() => await RunExclusiveAsync("正在处理…", async () =>
    {
        if (server.HasOwnedProcess) await server.StopAsync(AskStopDecisionAsync);
        else { await server.StartAsync(); branch = await maintenance.GetBranchAsync(); }
    });

    private async Task RestartAsync() => await RunExclusiveAsync("正在重启…", async () =>
    {
        if (server.HasOwnedProcess && !await server.StopAsync(AskStopDecisionAsync)) return;
        await server.StartAsync();
    });

    private async Task CheckUpdatesAsync() => await RunExclusiveAsync("正在检查更新…", async () =>
    {
        var result = await maintenance.CheckUpdatesAsync();
        ShowMessage(result, MessageBoxIcon.Information);
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
            if (manual) ShowMessage(result, MessageBoxIcon.Information);
            else if (result.Contains("已下载", StringComparison.Ordinal) && result != lastUpdateNotification)
            {
                lastUpdateNotification = result;
                tray.BalloonTipTitle = "管理器更新";
                tray.BalloonTipText = result;
                tray.ShowBalloonTip(5000);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("管理器更新", "检查失败：" + ex.Message);
            if (manual && !IsDisposed) ShowMessage("管理器更新检查失败：" + ex.Message, MessageBoxIcon.Warning);
        }
        finally { if (!IsDisposed) trayManagerUpdate.Text = "检查管理器更新"; }
    }

    private async Task UpdateAsync()
    {
        if (MessageBox.Show(this, "更新会停止服务并可能中断正在进行的聊天。\n将使用官方 Git 和 npm 命令。", "更新 SillyTavern", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        await RunExclusiveAsync("正在更新…", async () => { await maintenance.UpdateAsync(AskStopDecisionAsync); branch = await maintenance.GetBranchAsync(); });
    }

    private async Task RepairAsync()
    {
        if (MessageBox.Show(this, "依赖修复会停止服务，并运行官方 npm install 命令。", "依赖修复", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        await RunExclusiveAsync("正在安装依赖…", () => maintenance.RepairAsync(AskStopDecisionAsync));
    }

    private Task<StopDecision> AskStopDecisionAsync()
    {
        var result = MessageBox.Show(this, "服务尚未退出。\n\n选择“是”强制结束，“否”继续等待，“取消”中止当前操作。", "等待服务停止", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        return Task.FromResult(result switch { DialogResult.Yes => StopDecision.Force, DialogResult.No => StopDecision.Wait, _ => StopDecision.Cancel });
    }

    private async Task ExitAsync()
    {
        if (busy) { ShowMessage("请等待当前操作结束后再退出。", MessageBoxIcon.Information); return; }
        var canClose = false;
        await RunExclusiveAsync("正在退出…", async () => { canClose = !server.HasOwnedProcess || await server.StopAsync(AskStopDecisionAsync); });
        if (!canClose) return;
        managerUpdateLifetime.Cancel();
        var updateReady = managerUpdates is null || await managerUpdates.PrepareForExitAsync();
        if (updateReady) ManagerUpdateInstaller.TryLaunch(restart: false, autostart: false);
        reallyClosing = true;
        Close();
    }

    private void ShowSettings()
    {
        if (busy) return;
        using var dialog = new SettingsForm(settings, server.HasOwnedProcess);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (settings.AutoUpdateManager) { managerUpdateTimer.Start(); _ = CheckManagerUpdateAsync(manual: false); }
        else { managerUpdateTimer.Stop(); autoUpdateCheck.Cancel(); autoUpdateCheck.Dispose(); autoUpdateCheck = new CancellationTokenSource(); }
        _ = RunExclusiveAsync("正在读取设置…", async () => { await server.RefreshConfigAsync(); branch = await maintenance.GetBranchAsync(); });
    }

    private void OnServerChanged()
    {
        TryUi(() =>
        {
            if (server.State == ServerState.Failed)
            {
                tray.BalloonTipTitle = "SillyTavern 已退出";
                tray.BalloonTipText = server.Detail;
                tray.ShowBalloonTip(5000);
            }
            UpdateDisplay();
        });
    }

    private void UpdateDisplay()
    {
        if (IsDisposed) return;
        var running = server.HasOwnedProcess;
        status.Text = busy ? "●  " + busyLabel : "●  " + server.Detail;
        status.ForeColor = server.State switch
        {
            ServerState.Running => Color.FromArgb(40, 145, 95),
            ServerState.Failed or ServerState.Occupied => Color.FromArgb(195, 85, 64),
            ServerState.Starting or ServerState.Stopping => Color.FromArgb(207, 139, 45),
            _ => Color.FromArgb(100, 110, 123)
        };
        var config = server.Config;
        metadata.Text = config is null ? $"管理器 {ManagerVersion.Current}  ·  等待读取 ST 配置" : $"管理器 {ManagerVersion.Current}  ·  ST {config.Version}  ·  {branch}  ·  {config.Port}  ·  白名单{(config.Whitelist ? "开" : "关")}  ·  认证{(config.BasicAuth || config.UserAccounts ? "开" : "未开")}";
        startStop.Text = running ? "停止服务" : "启动服务";
        startStop.Enabled = !busy;
        restart.Enabled = !busy && running;
        open.Enabled = config is not null;
        checkUpdate.Enabled = update.Enabled = repair.Enabled = settingsButton.Enabled = !busy;
        trayStartStop.Text = startStop.Text; trayStartStop.Enabled = !busy;
        trayRestart.Enabled = !busy && running; trayUpdate.Enabled = !busy;
        tray.Text = ("SillyTavern：" + server.Detail)[..Math.Min(63, ("SillyTavern：" + server.Detail).Length)];
        SetTrayIcon(status.ForeColor);
        PopulateAddresses();
    }

    private void PopulateAddresses()
    {
        var config = server.Config;
        if (config is null) return;
        var selected = (addresses.SelectedItem as AddressItem)?.Url;
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
        if (addresses.Items.Cast<AddressItem>().Select(x => x.Url).SequenceEqual(items.Select(x => x.Url))) return;
        addresses.Items.Clear();
        addresses.Items.AddRange(items.Cast<object>().ToArray());
        addresses.SelectedIndex = Math.Max(0, items.FindIndex(x => x.Url == selected));
    }

    private sealed record AddressItem(string Name, string Url) { public override string ToString() => $"{Name}：{Url}"; }
    private void CopyAddress() { if (addresses.SelectedItem is AddressItem item) try { Clipboard.SetText(item.Url); } catch (ExternalException) { } }
    private void OpenServer() { if (server.Config is { } config) OpenPath(config.LocalUrl); else ShowMessage("尚未读取服务地址。", MessageBoxIcon.Information); }
    private void OpenPath(string path, bool create = false)
    {
        try
        {
            if (create) Directory.CreateDirectory(path);
            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("路径不存在。", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowMessage("无法打开：" + ex.Message, MessageBoxIcon.Warning); }
    }

    private void OnLogLine(string line)
    {
        queuedLines.Enqueue(line.Length > 8192 ? line[..8192] + "…（完整内容见日志文件）" : line);
        while (queuedLines.Count > 12000 && queuedLines.TryDequeue(out _)) Interlocked.Increment(ref omittedLiveLines);
    }
    private bool Matches(string line) => (sources.SelectedItem as string is not { } source || source == "全部" || line.Contains($"] [{source}] ", StringComparison.Ordinal))
        && (search.TextLength == 0 || line.Contains(search.Text, StringComparison.CurrentCultureIgnoreCase));
    private void FlushLogQueue()
    {
        var append = new StringBuilder();
        var trimmed = false;
        var omitted = Interlocked.Exchange(ref omittedLiveLines, 0);
        if (omitted > 0)
        {
            var notice = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 实时窗口省略 {omitted} 条日志，完整记录请查看日志文件。";
            logLines.Add(notice);
            if (Matches(notice)) append.AppendLine(notice);
        }
        for (var i = 0; i < 1000 && queuedLines.TryDequeue(out var line); i++)
        {
            logLines.Add(line);
            if (logLines.Count > 10000) { logLines.RemoveRange(0, Math.Min(500, logLines.Count - 10000 + 499)); trimmed = true; }
            if (Matches(line)) append.AppendLine(line);
        }
        if (trimmed) { RebuildLog(); return; }
        if (append.Length == 0) return;
        var oldSelection = logView.SelectionStart;
        var oldScroll = Point.Empty;
        if (pause.Checked) SendMessage(logView.Handle, 0x04DD, IntPtr.Zero, ref oldScroll); // EM_GETSCROLLPOS
        logView.AppendText(append.ToString());
        if (!pause.Checked) { logView.SelectionStart = logView.TextLength; logView.ScrollToCaret(); }
        else
        {
            logView.SelectionStart = Math.Min(oldSelection, logView.TextLength);
            SendMessage(logView.Handle, 0x04DE, IntPtr.Zero, ref oldScroll); // EM_SETSCROLLPOS
        }
    }
    private void RebuildLog()
    {
        var visible = logLines.Where(Matches).ToArray();
        logView.Text = visible.Length == 0 ? "" : string.Join(Environment.NewLine, visible) + Environment.NewLine;
        if (!pause.Checked) { logView.SelectionStart = logView.TextLength; logView.ScrollToCaret(); }
    }

    private void PositionAtBottomRight()
    {
        var area = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
        Location = new Point(Math.Max(area.Left, area.Right - Width - 12), Math.Max(area.Top, area.Bottom - Height - 12));
    }
    private void HideToTray() { Hide(); WindowState = FormWindowState.Normal; }
    private void RestoreWindow()
    {
        if (IsDisposed) return;
        Show(); WindowState = FormWindowState.Normal; Activate();
    }
    private void TryUi(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { } }
    private void ShowMessage(string text, MessageBoxIcon icon) => MessageBox.Show(this, text, "SillyTavern 管理器", MessageBoxButtons.OK, icon);

    private void SetTrayIcon(Color color)
    {
        using var image = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawIcon(Icon ?? SystemIcons.Application, 0, 0);
        using var outline = new SolidBrush(Color.White);
        using var dot = new SolidBrush(color);
        graphics.FillEllipse(outline, 18, 18, 14, 14);
        graphics.FillEllipse(dot, 20, 20, 10, 10);
        var handle = image.GetHicon();
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
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
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
            stopSignalThread = true; try { showSignal.Set(); } catch (ObjectDisposedException) { }
            logTimer.Stop(); logTimer.Dispose();
            managerUpdateTimer.Stop(); managerUpdateTimer.Dispose();
            managerUpdateLifetime.Cancel(); autoUpdateCheck.Cancel(); managerUpdates?.Dispose();
            log.LineWritten -= OnLogLine; server.Changed -= OnServerChanged;
            tray.Visible = false; tray.Dispose(); activeTrayIcon?.Dispose(); trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
