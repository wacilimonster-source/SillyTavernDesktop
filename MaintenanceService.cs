using SillyTavernTray.Ui;

namespace SillyTavernTray;

internal sealed class MaintenanceService
{
    private readonly AppSettings settings;
    private readonly AppLog log;
    private readonly ServerController server;
    private readonly SystemProxyResolver proxyResolver;
    public MaintenanceService(AppSettings settings, AppLog log, ServerController server, SystemProxyResolver? proxyResolver = null)
    { this.settings = settings; this.log = log; this.server = server; this.proxyResolver = proxyResolver ?? new SystemProxyResolver(); }

    /// <summary>
    /// Reports maintenance step transitions: index within the operation's step list,
    /// the new status, and an optional detail. Raised on background continuations.
    /// </summary>
    public event Action<int, StepStatus, string?>? StepChanged;

    // Carried between steps of one update operation; each RunStepAsync body is a separate lambda.
    private string? pendingNpmCommand;
    private IReadOnlyDictionary<string, string>? pendingNpmProxy;
    private IReadOnlyDictionary<string, string>? pendingGitProxy;

    private void Report(int index, StepStatus status, string? detail = null) => StepChanged?.Invoke(index, status, detail);

    /// <summary>Runs one maintenance step, reporting Active/Done/Failed around the body.</summary>
    private async Task RunStepAsync(int index, Func<Task> body)
    {
        Report(index, StepStatus.Active);
        try
        {
            await body();
            Report(index, StepStatus.Done);
        }
        catch (OperationCanceledException)
        {
            // A user cancel is not a step failure; the operation ends and the UI leaves the step bar.
            throw;
        }
        catch
        {
            Report(index, StepStatus.Failed);
            throw;
        }
    }

    private string DirectoryPath => settings.InstallDirectory;
    private string Git => CommandRunner.FindExecutable("git.exe") ?? throw new InvalidOperationException("未找到 Git，请检查 PATH。");
    private Task<CommandResult> GitAsync(params string[] args) => GitAsync(CancellationToken.None, args);
    private Task<CommandResult> GitAsync(CancellationToken cancellation, params string[] args) =>
        CommandRunner.RunAsync(Git, args, DirectoryPath, timeout: TimeSpan.FromSeconds(30), cancellation: cancellation);
    private static string ErrorText(CommandResult result) => string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();

    public async Task<string> GetBranchAsync()
    {
        try
        {
            var result = await GitAsync("branch", "--show-current");
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : "未知";
        }
        catch { return "未知"; }
    }

    private async Task CheckRepositoryAsync(bool requireClean, CancellationToken cancellation = default)
    {
        if (!Directory.Exists(DirectoryPath) || !File.Exists(Path.Combine(DirectoryPath, "server.js")))
            throw new InvalidOperationException("SillyTavern 目录无效，请先在设置中修正。");
        var repository = await GitAsync(cancellation, "rev-parse", "--is-inside-work-tree");
        if (repository.ExitCode != 0 || repository.Output.Trim() != "true") throw new InvalidOperationException("当前目录不是 Git 仓库，无法使用 Git 更新。");
        var branch = await GitAsync(cancellation, "branch", "--show-current");
        if (branch.ExitCode != 0 || string.IsNullOrWhiteSpace(branch.Output)) throw new InvalidOperationException("当前不是普通分支，请先检查 Git 状态。");
        var upstream = await GitAsync(cancellation, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (upstream.ExitCode != 0) throw new InvalidOperationException("当前分支没有可用的上游分支，请先检查 Git 配置。");
        if (!requireClean) return;
        var status = await GitAsync(cancellation, "status", "--porcelain", "--untracked-files=normal");
        if (status.ExitCode != 0) throw new InvalidOperationException("无法检查仓库状态：" + ErrorText(status));
        if (!string.IsNullOrWhiteSpace(status.Output)) throw new InvalidOperationException("仓库存在本地修改或未跟踪文件，请先处理后再更新。\n\n" + status.Output.Trim());
        foreach (var marker in new[] { "rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG" })
        {
            var pathResult = await GitAsync(cancellation, "rev-parse", "--git-path", marker);
            if (pathResult.ExitCode != 0) throw new InvalidOperationException("无法检查 Git 操作状态。");
            var path = Path.GetFullPath(pathResult.Output.Trim(), DirectoryPath);
            if (File.Exists(path) || Directory.Exists(path)) throw new InvalidOperationException("存在未完成的 Git 操作（" + marker + "），请先处理。");
        }
    }

    public async Task<string> CheckUpdatesAsync(CancellationToken cancellation = default)
    {
        await CheckRepositoryAsync(requireClean: false, cancellation);
        var proxy = await GitProxyEnvironmentAsync(cancellation);
        log.Write("维护", "检查更新：git fetch");
        var fetch = await CommandRunner.RunAsync(Git, ["fetch"], DirectoryPath, s => log.Write("维护", s), environment: proxy,
            cancellation: cancellation, timeout: TimeSpan.FromMinutes(5));
        if (fetch.ExitCode != 0) throw new InvalidOperationException("检查更新失败：" + ErrorText(fetch));
        var result = await GitAsync(cancellation, "rev-list", "--left-right", "--count", "HEAD...@{u}");
        var counts = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (result.ExitCode != 0 || counts.Length < 2 || !int.TryParse(counts[0], out var ahead) || !int.TryParse(counts[1], out var behind))
            throw new InvalidOperationException("无法比较当前分支与上游：" + ErrorText(result));
        var message = (ahead, behind) switch
        {
            (0, 0) => "当前分支已与上游同步。",
            (> 0, > 0) => $"本地和上游存在分歧（本地领先 {ahead}，上游领先 {behind}），请先检查。",
            (> 0, 0) => $"本地领先上游 {ahead} 个提交。",
            _ => $"发现更新：上游领先 {behind} 个提交。"
        };
        log.Write("维护", message);
        return message;
    }

    private async Task EnsureStoppedAndPortFreeAsync(Func<Task<StopDecision>> ask)
    {
        await server.RefreshConfigAsync();
        if (server.HasOwnedProcess && !await server.StopAsync(ask)) throw new OperationCanceledException("服务尚未停止，维护操作已取消。");
        for (var i = 0; i < 15 && ServerController.PortIsOccupied(server.Config!.Port); i++) await Task.Delay(200);
        if (ServerController.PortIsOccupied(server.Config!.Port)) throw new InvalidOperationException("端口仍被占用，可能存在外部实例。请先停止外部实例，管理器不会自动结束它。");
    }

    public async Task UpdateAsync(Func<Task<StopDecision>> ask, CancellationToken cancellation = default)
    {
        // Preflight is cancellable; once the service has been stopped the operation runs to completion.
        await RunStepAsync(0, async () =>
        {
            await CheckRepositoryAsync(requireClean: true, cancellation); // Preflight does not interrupt a running service.
            pendingNpmCommand = CommandRunner.FindExecutable("npm.cmd") ?? throw new InvalidOperationException("未找到 npm，请检查 Node.js 安装。");
            pendingGitProxy = await GitProxyEnvironmentAsync(cancellation);
            pendingNpmProxy = await NpmProxyEnvironmentAsync(pendingNpmCommand, cancellation);
        });
        await RunStepAsync(1, async () =>
        {
            await EnsureStoppedAndPortFreeAsync(ask);
            log.Write("维护", "开始更新：git pull --rebase --autostash");
        });
        await RunStepAsync(2, async () =>
        {
            var pull = await CommandRunner.RunAsync(Git, ["pull", "--rebase", "--autostash"], DirectoryPath, s => log.Write("维护", s), environment: pendingGitProxy,
                timeout: TimeSpan.FromMinutes(15));
            if (pull.ExitCode != 0) throw new InvalidOperationException("Git 更新失败，服务保持停止。\n" + ErrorText(pull));
        });
        await RunStepAsync(3, async () => await InstallDependenciesAsync(pendingNpmCommand!, pendingNpmProxy!));
        await RunStepAsync(4, async () =>
        {
            log.Write("维护", "更新完成，正在重新启动服务。");
            await server.StartAsync();
        });
    }

    public async Task RepairAsync(Func<Task<StopDecision>> ask, CancellationToken cancellation = default)
    {
        string npm = "";
        IReadOnlyDictionary<string, string>? npmProxy = null;
        await RunStepAsync(0, async () =>
        {
            npm = CommandRunner.FindExecutable("npm.cmd") ?? throw new InvalidOperationException("未找到 npm，请检查 Node.js 安装。");
            npmProxy = await NpmProxyEnvironmentAsync(npm, cancellation);
        });
        await RunStepAsync(1, () => EnsureStoppedAndPortFreeAsync(ask));
        await RunStepAsync(2, async () => await InstallDependenciesAsync(npm, npmProxy!));
        await RunStepAsync(3, async () =>
        {
            log.Write("维护", "依赖安装完成，正在重新启动服务。");
            await server.StartAsync();
        });
    }

    private async Task InstallDependenciesAsync(string npm, IReadOnlyDictionary<string, string> proxy)
    {
        log.Write("维护", "安装依赖：npm install --no-save --no-audit --no-fund --loglevel=error --no-progress --omit=dev --ignore-scripts");
        var result = await CommandRunner.RunNpmInstallAsync(npm, DirectoryPath, s => log.Write("维护", s), proxy);
        if (result.ExitCode != 0) throw new InvalidOperationException("依赖安装失败，服务保持停止。\n" + ErrorText(result));
    }

    private async Task<IReadOnlyDictionary<string, string>?> GitProxyEnvironmentAsync(CancellationToken cancellation = default)
    {
        var branch = await GitAsync(cancellation, "branch", "--show-current");
        var remote = await GitAsync(cancellation, "config", "--get", $"branch.{branch.Output.Trim()}.remote");
        if (branch.ExitCode != 0 || remote.ExitCode != 0 || string.IsNullOrWhiteSpace(remote.Output))
            throw new InvalidOperationException("无法读取当前分支的 Git 远端，未执行网络操作。");
        var remoteName = remote.Output.Trim();
        if (remoteName == ".") { log.Write("维护", "Git 使用本地远端，保留现有网络设置。"); return null; }
        var url = await GitAsync(cancellation, "remote", "get-url", "--", remoteName);
        if (url.ExitCode != 0 || string.IsNullOrWhiteSpace(url.Output))
            throw new InvalidOperationException("无法读取 Git 远端地址，未执行网络操作。");
        if (!Uri.TryCreate(url.Output.Trim(), UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "https"))
        {
            log.Write("维护", "Git 远端不是 HTTP(S)，沿用其现有网络或 SSH 设置。");
            return null;
        }
        var route = await proxyResolver.ResolveAsync(destination);
        log.Write("维护", "Git 网络：" + route.Description + "。");
        return SystemProxyResolver.ForGit(route);
    }

    private async Task<IReadOnlyDictionary<string, string>> NpmProxyEnvironmentAsync(string npm, CancellationToken cancellation = default)
    {
        var result = await CommandRunner.GetNpmRegistryAsync(npm, DirectoryPath, cancellation);
        if (result.ExitCode != 0 || !Uri.TryCreate(result.Output.Trim(), UriKind.Absolute, out var registry) || registry.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("无法读取 npm 的 HTTP(S) 仓库地址，未执行依赖安装。");
        var httpUri = new UriBuilder(registry) { Scheme = "http", Port = registry.IsDefaultPort ? -1 : registry.Port }.Uri;
        var httpsUri = new UriBuilder(registry) { Scheme = "https", Port = registry.IsDefaultPort ? -1 : registry.Port }.Uri;
        var routes = await Task.WhenAll(proxyResolver.ResolveAsync(httpUri), proxyResolver.ResolveAsync(httpsUri));
        var route = registry.Scheme == "https" ? routes[1] : routes[0];
        log.Write("维护", "npm 网络：" + route.Description + "。");
        return proxyResolver.ForNpm(routes[0], routes[1], registry);
    }
}
