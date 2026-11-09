using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;

namespace SillyTavernTray;

internal sealed class SettingsForm : ThemedForm
{
    private readonly AppSettings settings;
    private readonly bool serviceRunning;

    private readonly Panel body;
    private readonly Panel footer;
    private readonly TextField directory = new();
    private readonly ThemedLabel errorLabel = new() { Tone = LabelTone.Danger, AutoSize = false, Visible = false };
    private readonly ThemedLabel hintLabel = new() { Tone = LabelTone.Tertiary, AutoSize = false };
    private readonly ToggleSwitch login = new();
    private readonly ToggleSwitch start = new();
    private readonly ToggleSwitch browser = new();
    private readonly ToggleSwitch autoUpdate = new();
    private readonly ToggleSwitch watchdog = new();
    private readonly ThemedLabel browserLabel = new() { Tone = LabelTone.Secondary, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ThemedLabel updateStatus = new() { Tone = LabelTone.Secondary, AutoSize = false, Visible = false };
    private readonly ThemedButton checkManagerUpdate = new();
    private readonly ThemedButton saveButton = new();
    private readonly List<Panel> dividers = new();

    static SettingsForm() { Theme.Initialize(); }

    public SettingsForm(AppSettings settings, bool serviceRunning)
        : base(resizable: false)
    {
        this.settings = settings;
        this.serviceRunning = serviceRunning;
        Theme.Initialize();
        Text = "设置";
        Font = Typography.Body;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        _ = Handle;
        var scale = Math.Max(1f, DeviceDpi / 96f);
        ClientSize = new Size((int)Math.Round(560 * scale), (int)Math.Round(710 * scale));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Current.Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(52)));
        Controls.Add(root);

        var titleBar = new TitleBar { Title = "设置", Version = "", AppIcon = null };
        titleBar.ShowSettingsButton = false;
        titleBar.ShowMinimizeButton = false;
        titleBar.CloseClicked += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        root.Controls.Add(titleBar, 0, 0);

        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Background, Padding = new Padding(S(20), S(12), S(20), S(8)), AutoScroll = true };
        root.Controls.Add(body, 0, 1);

        footer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Background };
        footer.Paint += (_, e) => { using var pen = new Pen(Theme.Current.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var footerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, S(9), S(20), 0) };
        saveButton.Text = "保存";
        saveButton.Variant = ButtonVariant.Accent;
        saveButton.PreferredHeight = 34;
        saveButton.Margin = new Padding(S(8), 0, 0, 0);
        saveButton.Click += (_, _) => SaveSettings();
        var cancel = new ThemedButton { Text = "取消", Variant = ButtonVariant.Quiet, PreferredHeight = 34, DialogResult = DialogResult.Cancel };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footerActions.Controls.Add(saveButton);
        footerActions.Controls.Add(cancel);
        footer.Controls.Add(footerActions);
        root.Controls.Add(footer, 0, 2);
        AcceptButton = saveButton;
        CancelButton = cancel;

        BuildBody();
        ApplyTheme();
    }

    private int S(int logical) => Draw.Scale(this, logical);

    private void BuildBody()
    {
        var width = Math.Max(S(300), ClientSize.Width - S(44));
        var y = S(6);

        var installTitle = SectionTitle("安装位置");
        installTitle.Location = new Point(0, y);
        installTitle.Width = width;
        body.Controls.Add(installTitle);
        y += S(26);

        directory.SetBounds(0, y, width - S(88), S(36));
        directory.Text = settings.InstallDirectory;
        directory.AccessibleName = "SillyTavern 安装目录";
        directory.InnerBox.AccessibleName = "SillyTavern 安装目录";
        body.Controls.Add(directory);
        var browse = new ThemedButton
        {
            Text = "浏览…",
            Variant = ButtonVariant.Secondary,
            IconGlyph = Icons.Folder,
            PreferredHeight = 36,
            AutoSize = false,
            Location = new Point(width - S(80), y)
        };
        browse.Size = new Size(S(80), S(36));
        browse.Click += (_, _) => Browse(browse);
        body.Controls.Add(browse);
        y += S(44);

        hintLabel.Text = "目录中需包含 server.js；更换目录前需停止服务。";
        hintLabel.SetBounds(0, y, width, S(18));
        body.Controls.Add(hintLabel);
        y += S(22);

        errorLabel.SetBounds(0, y, width, S(34));
        body.Controls.Add(errorLabel);
        y += S(38);

        y = AddDivider(y);
        y += S(10);

        var startTitle = SectionTitle("启动行为");
        startTitle.Location = new Point(0, y);
        startTitle.Width = width;
        body.Controls.Add(startTitle);
        y += S(28);

        login.Checked = ReadAutoStart();
        AddToggleRow(new ThemedLabel(), "登录 Windows 后启动管理器", width, y, login);
        y += S(38);

        AddToggleRow(browserLabel, "登录自启时自动打开浏览器", width, y, browser, S(24));
        y += S(38);

        start.Checked = settings.StartServerOnLaunch;
        AddToggleRow(new ThemedLabel(), "启动管理器后自动启动 SillyTavern", width, y, start);
        y += S(38);

        watchdog.Checked = settings.AutoRestartServer;
        AddToggleRow(new ThemedLabel(), "服务崩溃或心跳超时后自动重启（看门狗）", width, y, watchdog);
        y += S(38);

        browser.Checked = settings.OpenBrowserOnAutostart;
        browser.CheckedChanged += (_, _) => ApplyDependentState();
        login.CheckedChanged += (_, _) => ApplyDependentState();
        ApplyDependentState();

        y = AddDivider(y);
        y += S(10);

        var updateTitle = SectionTitle("更新");
        updateTitle.Location = new Point(0, y);
        updateTitle.Width = width;
        body.Controls.Add(updateTitle);
        y += S(28);

        autoUpdate.Checked = settings.AutoUpdateManager;
        AddToggleRow(new ThemedLabel(), "自动检查并下载管理器更新", width, y, autoUpdate);
        y += S(40);

        var version = new ThemedLabel { Text = $"当前版本 v{ManagerVersion.Current}", Tone = LabelTone.Secondary, AutoSize = false, Location = new Point(0, y + S(8)), Size = new Size(width - S(170), S(24)) };
        body.Controls.Add(version);
        checkManagerUpdate.Text = "检查管理器更新";
        checkManagerUpdate.Variant = ButtonVariant.Secondary;
        checkManagerUpdate.IconGlyph = Icons.Restart;
        checkManagerUpdate.PreferredHeight = 30;
        checkManagerUpdate.AutoSize = false;
        checkManagerUpdate.Size = new Size(S(156), S(30));
        checkManagerUpdate.Location = new Point(width - S(156), y + S(4));
        checkManagerUpdate.Click += async (_, _) => await CheckManagerUpdateAsync();
        body.Controls.Add(checkManagerUpdate);
        y += S(40);

        updateStatus.SetBounds(0, y, width, S(34));
        body.Controls.Add(updateStatus);
        if (!updateStatus.Visible) y += 0;

        y = AddDivider(y + S(40));
        y += S(10);

        var aboutTitle = SectionTitle("关于");
        aboutTitle.Location = new Point(0, y);
        aboutTitle.Width = width;
        body.Controls.Add(aboutTitle);
        y += S(26);

        var about = new ThemedLabel
        {
            Text = "本程序不修改 SillyTavern 配置；日志与设置存于 %LOCALAPPDATA%\\SillyTavernTray。",
            Tone = LabelTone.Tertiary,
            AutoSize = false,
            Location = new Point(0, y),
            Size = new Size(width, S(36))
        };
        body.Controls.Add(about);
        body.AutoScrollMinSize = new Size(0, y + S(52));
    }

    private ThemedLabel SectionTitle(string text) => new()
    {
        Text = text,
        Font = Typography.Section,
        Tone = LabelTone.Secondary,
        AutoSize = false,
        Height = S(20),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private void AddToggleRow(ThemedLabel label, string text, int width, int y, ToggleSwitch toggle, int indent = 0)
    {
        label.Text = text;
        label.Tone = LabelTone.Secondary;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Location = new Point(indent, y + S(8));
        label.Size = new Size(width - indent - S(52), S(24));
        label.Cursor = Cursors.Hand;
        label.Click += (_, _) => { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
        toggle.AccessibleName = text;
        toggle.Location = new Point(width - S(40), y + S(6));
        body.Controls.Add(label);
        body.Controls.Add(toggle);
    }

    private int AddDivider(int y)
    {
        var divider = new Panel { Location = new Point(0, y), Size = new Size(Math.Max(S(300), ClientSize.Width - S(44)), 1), BackColor = Theme.Current.Border };
        dividers.Add(divider);
        body.Controls.Add(divider);
        return y + S(1);
    }

    private bool ReadAutoStart()
    {
        try { return AutoStart.IsEnabled(); }
        catch { return false; }
    }

    private void ApplyDependentState()
    {
        var enabled = login.Checked && login.Enabled;
        browser.Enabled = enabled;
        browserLabel.Tone = enabled ? LabelTone.Secondary : LabelTone.Disabled;
    }

    private void Browse(Control anchor)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 server.js 的 SillyTavern 目录",
            SelectedPath = Directory.Exists(directory.Text) ? directory.Text : ""
        };
        anchor.Enabled = false;
        try { if (dialog.ShowDialog(this) == DialogResult.OK) directory.Text = dialog.SelectedPath; }
        finally { anchor.Enabled = true; }
    }

    private async Task CheckManagerUpdateAsync()
    {
        checkManagerUpdate.Enabled = false;
        checkManagerUpdate.Text = "正在检查…";
        updateStatus.Visible = true;
        updateStatus.Tone = LabelTone.Secondary;
        updateStatus.Text = "正在检查管理器更新…";
        try
        {
            using var service = new ManagerUpdateService(new AppLog());
            var result = await service.CheckAsync(manual: true);
            updateStatus.Text = result;
            updateStatus.Tone = result.Contains("失败", StringComparison.Ordinal) ? LabelTone.Danger : LabelTone.Secondary;
        }
        catch (Exception ex)
        {
            updateStatus.Text = "检查失败：" + ex.Message;
            updateStatus.Tone = LabelTone.Danger;
        }
        finally
        {
            checkManagerUpdate.Enabled = true;
            checkManagerUpdate.Text = "检查管理器更新";
        }
    }

    private void SaveSettings()
    {
        try
        {
            errorLabel.Visible = false;
            var path = Path.GetFullPath(directory.Text.Trim());
            if (!File.Exists(Path.Combine(path, "server.js")))
            {
                ShowError("该目录中没有 server.js，请选择已有的 SillyTavern 目录。");
                directory.InnerBox.Focus();
                return;
            }
            if (serviceRunning && !string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), settings.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                ShowError("请先停止当前服务，再更换安装目录。");
                return;
            }
            var candidate = new AppSettings
            {
                InstallDirectory = path,
                StartServerOnLaunch = start.Checked,
                OpenBrowserOnAutostart = browser.Checked,
                AutoUpdateManager = autoUpdate.Checked,
                AutoRestartServer = watchdog.Checked
            };
            var previousAutostart = ReadAutoStart();
            candidate.Save();
            try { AutoStart.SetEnabled(login.Checked); }
            catch
            {
                settings.Save();
                try { AutoStart.SetEnabled(previousAutostart); } catch { }
                throw;
            }
            settings.InstallDirectory = candidate.InstallDirectory;
            settings.StartServerOnLaunch = candidate.StartServerOnLaunch;
            settings.OpenBrowserOnAutostart = candidate.OpenBrowserOnAutostart;
            settings.AutoUpdateManager = candidate.AutoUpdateManager;
            settings.AutoRestartServer = candidate.AutoRestartServer;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            ShowError("无法保存设置：" + ex.Message);
        }
    }

    private void ShowError(string message)
    {
        errorLabel.Text = message;
        errorLabel.Visible = true;
        errorLabel.Invalidate();
    }

    protected override void ApplyTheme()
    {
        if (body is null) return;
        var p = Theme.Current;
        BackColor = p.Border;
        body.BackColor = p.Background;
        footer.BackColor = p.Background;
        foreach (var divider in dividers) divider.BackColor = p.Border;
    }
}
