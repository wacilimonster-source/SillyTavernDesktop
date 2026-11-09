namespace SillyTavernTray;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings settings;
    private readonly bool serviceRunning;
    private readonly TextBox directory = new() { Dock = DockStyle.Fill };
    private readonly CheckBox login = new() { Text = "登录 Windows 后启动管理器", AutoSize = true };
    private readonly CheckBox start = new() { Text = "启动管理器后启动 SillyTavern", AutoSize = true };
    private readonly CheckBox browser = new() { Text = "登录自启时自动打开浏览器", AutoSize = true };
    private readonly CheckBox autoUpdate = new() { Text = "自动检查并下载管理器更新", AutoSize = true };

    public SettingsForm(AppSettings settings, bool serviceRunning)
    {
        this.settings = settings; this.serviceRunning = serviceRunning;
        Text = "管理器设置"; Font = new Font("Microsoft YaHei UI", 9);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; ClientSize = new Size(530, 265);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        var label = new Label { Text = "SillyTavern 安装目录", AutoSize = true };
        layout.Controls.Add(label, 0, 0); layout.SetColumnSpan(label, 2);
        directory.Text = settings.InstallDirectory;
        layout.Controls.Add(directory, 0, 1);
        var browse = new Button { Text = "浏览…", Dock = DockStyle.Top, Height = 26 };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择包含 server.js 的 SillyTavern 目录", SelectedPath = Directory.Exists(directory.Text) ? directory.Text : "" };
            if (dialog.ShowDialog(this) == DialogResult.OK) directory.Text = dialog.SelectedPath;
        };
        layout.Controls.Add(browse, 1, 1);
        try { login.Checked = AutoStart.IsEnabled(); } catch { login.Checked = false; }
        start.Checked = settings.StartServerOnLaunch; browser.Checked = settings.OpenBrowserOnAutostart; autoUpdate.Checked = settings.AutoUpdateManager;
        layout.Controls.Add(login, 0, 2); layout.SetColumnSpan(login, 2);
        layout.Controls.Add(start, 0, 3); layout.SetColumnSpan(start, 2);
        layout.Controls.Add(browser, 0, 4); layout.SetColumnSpan(browser, 2);
        layout.Controls.Add(autoUpdate, 0, 5); layout.SetColumnSpan(autoUpdate, 2);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, WrapContents = false };
        var save = new Button { Text = "保存", Width = 75, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "取消", Width = 75, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => SaveSettings();
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2);
        AcceptButton = save; CancelButton = cancel;
    }

    private void SaveSettings()
    {
        try
        {
            var path = Path.GetFullPath(directory.Text.Trim());
            if (!File.Exists(Path.Combine(path, "server.js"))) throw new InvalidOperationException("该目录中没有 server.js，请选择已有的 SillyTavern 目录。");
            if (serviceRunning && !string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), settings.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请先停止当前服务，再更换安装目录。");
            var candidate = new AppSettings { InstallDirectory = path, StartServerOnLaunch = start.Checked, OpenBrowserOnAutostart = browser.Checked, AutoUpdateManager = autoUpdate.Checked };
            var previousAutostart = AutoStart.IsEnabled();
            candidate.Save();
            try { AutoStart.SetEnabled(login.Checked); }
            catch { settings.Save(); try { AutoStart.SetEnabled(previousAutostart); } catch { } throw; }
            settings.InstallDirectory = candidate.InstallDirectory;
            settings.StartServerOnLaunch = candidate.StartServerOnLaunch;
            settings.OpenBrowserOnAutostart = candidate.OpenBrowserOnAutostart;
            settings.AutoUpdateManager = candidate.AutoUpdateManager;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存设置", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
