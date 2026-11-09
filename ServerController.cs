using System.Net;
using System.Net.NetworkInformation;

namespace SillyTavernTray;

internal enum ServerState { Stopped, Starting, Running, Stopping, Failed, Occupied }
internal enum StopDecision { Wait, Cancel, Force }

internal sealed class ServerController
{
    private readonly AppSettings settings;
    private readonly AppLog log;
    private readonly object sync = new();
    private NativeNodeProcess? child;
    private bool expectedExit;
    private TaskCompletionSource<bool>? listening;
    private ServerState state = ServerState.Stopped;
    private string detail = "未运行";
    public InstanceConfig? Config { get; private set; }
    public event Action? Changed;

    public ServerController(AppSettings settings, AppLog log) { this.settings = settings; this.log = log; }
    public ServerState State { get { lock (sync) return state; } }
    public string Detail { get { lock (sync) return detail; } }
    public bool HasOwnedProcess { get { lock (sync) return child is { HasExited: false }; } }

    private void SetState(ServerState next, string message)
    {
        lock (sync) { state = next; detail = message; }
        Changed?.Invoke();
    }

    public async Task RefreshConfigAsync()
    {
        var directory = settings.InstallDirectory;
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "server.js")))
            throw new InvalidOperationException("未找到 SillyTavern 目录或 server.js，请在设置中选择已有目录。");
        var node = CommandRunner.FindExecutable("node.exe") ?? throw new InvalidOperationException("未找到 Node.js，请检查 PATH。");
        Config = await InstanceConfig.LoadAsync(directory, node);
        Changed?.Invoke();
    }

    public static bool PortIsOccupied(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port); }
        catch (NetworkInformationException) { throw new InvalidOperationException("无法检查端口状态，请先排查网络环境。"); }
    }

    public async Task StartAsync(bool isAutostart = false)
    {
        if (HasOwnedProcess) return;
        await RefreshConfigAsync();
        var config = Config!;
        if (PortIsOccupied(config.Port))
        {
            SetState(ServerState.Occupied, $"端口 {config.Port} 已被占用，可能已有其他实例");
            log.Write("管理器", $"端口 {config.Port} 已被占用。不会结束或接管其他进程。");
            return;
        }
        var node = CommandRunner.FindExecutable("node.exe")!;
        var args = new List<string> { "server.js", "--listen" };
        if (isAutostart) { args.Add("--browserLaunchEnabled"); args.Add(settings.OpenBrowserOnAutostart ? "true" : "false"); }
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { listening = ready; expectedExit = false; }
        SetState(ServerState.Starting, "正在启动，等待服务响应…");
        log.Write("管理器", "正在后台启动 SillyTavern（--listen）。");
        NativeNodeProcess launched;
        try
        {
            launched = NativeNodeProcess.Start(node, settings.InstallDirectory, args, text =>
            {
                log.Write("服务", text);
                if (text.Contains("SillyTavern is listening on", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult(true);
            });
        }
        catch (Exception ex)
        {
            SetState(ServerState.Failed, "启动失败：" + ex.Message);
            log.Write("管理器", "启动失败：" + ex.Message);
            return;
        }
        lock (sync) child = launched;
        launched.BeginMonitoring(code => OnExit(launched, code));
        _ = ObserveReadinessAsync(launched, ready.Task, config);
    }

    private async Task ObserveReadinessAsync(NativeNodeProcess process, Task ready, InstanceConfig config)
    {
        try { await ready.WaitAsync(TimeSpan.FromSeconds(90)); }
        catch (OperationCanceledException) { return; }
        catch (TimeoutException)
        {
            if (!process.HasExited && ReferenceEquals(child, process) && State == ServerState.Starting)
                SetState(ServerState.Starting, "服务启动较慢，请查看日志");
            return;
        }
        using var handler = new HttpClientHandler();
        if (config.Ssl) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; // Only the loopback readiness probe.
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 15 && !process.HasExited; i++)
        {
            try
            {
                using var response = await client.GetAsync(config.LocalUrl);
                if (!ReferenceEquals(child, process) || State != ServerState.Starting) return;
                var restricted = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                SetState(ServerState.Running, restricted ? $"运行中，本机访问受限（{(int)response.StatusCode}）" : "运行中");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { await Task.Delay(1000); }
        }
        if (!process.HasExited && ReferenceEquals(child, process) && State == ServerState.Starting)
            SetState(ServerState.Running, "已监听，本机访问未验证");
    }

    private void OnExit(NativeNodeProcess exited, int code)
    {
        bool wasExpected;
        lock (sync)
        {
            if (!ReferenceEquals(child, exited)) return;
            child = null;
            wasExpected = expectedExit;
            expectedExit = false;
            listening?.TrySetCanceled();
        }
        log.Write("管理器", wasExpected ? $"服务已停止（退出码 {code}）。" : $"服务已退出（退出码 {code}）。");
        SetState(wasExpected ? ServerState.Stopped : ServerState.Failed, wasExpected ? "未运行" : $"服务异常退出（退出码 {code}）");
        // Allow the asynchronous pipe readers to drain their final output before disposal.
        _ = Task.Run(async () => { await Task.Delay(500); exited.Dispose(); });
    }

    public async Task<bool> StopAsync(Func<Task<StopDecision>> askOnTimeout)
    {
        NativeNodeProcess? process;
        lock (sync) { process = child; if (process is not null) expectedExit = true; }
        if (process is null || process.HasExited) return true;
        SetState(ServerState.Stopping, "正在停止服务…");
        log.Write("管理器", "正在请求服务正常退出。");
        try
        {
            if (!await process.SendInterruptAsync()) log.Write("管理器", "无法发送正常停止信号。");
        }
        catch (Exception ex) { log.Write("管理器", "无法发送正常停止信号：" + ex.Message); }
        while (!await process.WaitForExitAsync(TimeSpan.FromSeconds(8)))
        {
            var decision = await askOnTimeout();
            if (decision == StopDecision.Cancel)
            {
                if (!process.HasExited) SetState(ServerState.Running, "停止请求尚未完成");
                return false;
            }
            if (decision == StopDecision.Force)
            {
                log.Write("管理器", "用户选择强制结束本管理器启动的服务进程。");
                try { process.Kill(); }
                catch (Exception ex) { log.Write("管理器", "强制结束失败：" + ex.Message); return false; }
                if (!await process.WaitForExitAsync(TimeSpan.FromSeconds(8))) return false;
                break;
            }
        }
        return true;
    }
}
