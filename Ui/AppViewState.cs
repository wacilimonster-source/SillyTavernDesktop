namespace SillyTavernTray.Ui;

internal enum BusyOperation { None, ReadingConfig, Starting, Stopping, Updating, Repairing, CheckingUpdates, Exiting }
internal enum ConfigurationState { Unconfigured, Pending, Configured, Error }
internal enum PrimaryActionKind { None, OpenServer, Start, Retry, ChooseDirectory }
internal enum ViewActionKind { Stop, Restart, OpenServer, OpenSettings, ViewLog }

internal sealed record ViewAction(string Label, string Icon, ViewActionKind Kind, bool Danger = false, bool Enabled = true);

internal sealed record AppViewState(
    StatusTone Tone,
    string Title,
    string Metadata,
    string? Note,
    PrimaryActionKind PrimaryKind,
    string PrimaryLabel,
    string PrimaryIcon,
    bool ShowProgress,
    string ProgressLabel,
    IReadOnlyList<ViewAction> Actions,
    bool AddressEnabled,
    bool OnboardingVisible);

internal static class AppStateFactory
{
    public static AppViewState Compute(
        ServerState server,
        bool hasOwnedProcess,
        BusyOperation busy,
        ConfigurationState configuration,
        string detail,
        string metadata)
    {
        if (busy != BusyOperation.None)
        {
            var title = busy switch
            {
                BusyOperation.ReadingConfig => "正在检查环境…",
                BusyOperation.Starting => "正在启动…",
                BusyOperation.Stopping => "正在停止…",
                BusyOperation.Updating => "正在更新 SillyTavern…",
                BusyOperation.Repairing => "正在安装依赖…",
                BusyOperation.CheckingUpdates => "正在检查更新…",
                BusyOperation.Exiting => "正在退出…",
                _ => "正在处理…"
            };
            return new AppViewState(StatusTone.Busy, title, metadata, null,
                PrimaryActionKind.None, "", "", true, title,
                Array.Empty<ViewAction>(), false, false);
        }

        if (configuration == ConfigurationState.Unconfigured)
            return new AppViewState(StatusTone.Stopped, "未配置", "尚未选择包含 server.js 的 SillyTavern 目录", null,
                PrimaryActionKind.ChooseDirectory, "选择目录", Icons.Folder, false, "",
                [new ViewAction("打开设置", Icons.Settings, ViewActionKind.OpenSettings)], false, true);

        if (configuration == ConfigurationState.Error)
            return new AppViewState(StatusTone.Error, "配置读取失败", metadata, detail,
                PrimaryActionKind.Retry, "重新读取", Icons.Restart, false, "",
                [new ViewAction("打开设置", Icons.Settings, ViewActionKind.OpenSettings),
                 new ViewAction("查看日志", Icons.History, ViewActionKind.ViewLog)], false, false);

        if (server == ServerState.Occupied)
            return new AppViewState(StatusTone.Error, "端口被占用", metadata, detail,
                PrimaryActionKind.Retry, "重新检测", Icons.Restart, false, "",
                [new ViewAction("查看日志", Icons.History, ViewActionKind.ViewLog)], false, false);

        if (server == ServerState.Failed)
            return new AppViewState(StatusTone.Error, "服务异常退出", metadata, detail,
                PrimaryActionKind.Start, "启动服务", Icons.Play, false, "",
                [new ViewAction("查看日志", Icons.History, ViewActionKind.ViewLog)], false, false);

        if (server is ServerState.Starting or ServerState.Stopping)
            return new AppViewState(StatusTone.Busy, detail, metadata, null,
                PrimaryActionKind.None, "", "", true, detail,
                Array.Empty<ViewAction>(), false, false);

        if (hasOwnedProcess || server == ServerState.Running)
        {
            var restricted = detail.Contains("受限", StringComparison.Ordinal);
            return new AppViewState(StatusTone.Running, restricted ? "运行中（受限）" : "运行中", metadata,
                restricted ? detail : null,
                PrimaryActionKind.OpenServer, "打开 SillyTavern", Icons.OpenExternal, false, "",
                [new ViewAction("停止服务", Icons.Stop, ViewActionKind.Stop, Danger: true),
                 new ViewAction("重启", Icons.Restart, ViewActionKind.Restart)], true, false);
        }

        return new AppViewState(StatusTone.Stopped, "已停止", metadata, null,
            PrimaryActionKind.Start, "启动服务", Icons.Play, false, "",
            [new ViewAction("打开 SillyTavern", Icons.OpenExternal, ViewActionKind.OpenServer, Enabled: false)], false, false);
    }

    public static string BusyTitle(BusyOperation busy) => busy switch
    {
        BusyOperation.ReadingConfig => "正在检查环境…",
        BusyOperation.Starting => "正在启动…",
        BusyOperation.Stopping => "正在停止…",
        BusyOperation.Updating => "正在更新 SillyTavern…",
        BusyOperation.Repairing => "正在安装依赖…",
        BusyOperation.CheckingUpdates => "正在检查更新…",
        BusyOperation.Exiting => "正在退出…",
        _ => ""
    };
}
