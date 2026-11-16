using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SillyTavernTray;

internal enum ServerState { Stopped, Starting, Running, Stopping, Failed, Occupied }
internal enum StopDecision { Wait, Cancel, Force }

internal sealed class ServerController
{
    private readonly AppSettings settings;
    private readonly AppLog log;
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim configGate = new(1, 1);
    private NativeNodeProcess? child;
    private bool expectedExit;
    private ServerState state = ServerState.Stopped;
    private string detail = "未运行";
    private int stopWaitedSeconds;
    private int? lastExitCode;
    public InstanceConfig? Config { get; private set; }
    public event Action? Changed;
    /// <summary>State-independent observations (slow startup) that deserve a tray balloon.</summary>
    public event Action<string>? Notice;

    public ServerController(AppSettings settings, AppLog log) { this.settings = settings; this.log = log; }
    public ServerState State { get { lock (sync) return state; } }
    public string Detail { get { lock (sync) return detail; } }
    public bool HasOwnedProcess { get { lock (sync) return child is { HasExited: false }; } }
    /// <summary>Seconds elapsed since the current stop request started; 0 while not stopping.</summary>
    public int StopWaitedSeconds { get { lock (sync) return stopWaitedSeconds; } }
    /// <summary>Exit code of the last unexpected service exit, for the diagnostics view.</summary>
    public int? LastExitCode { get { lock (sync) return lastExitCode; } }
    public string? HeartbeatPath => Config is null ? null : Path.Combine(Config.DataRoot, "heartbeat.json");

    private void SetState(ServerState next, string message)
    {
        lock (sync) { state = next; detail = message; }
        Changed?.Invoke();
    }

    public async Task RefreshConfigAsync()
    {
        await configGate.WaitAsync();
        try
        {
            var directory = settings.InstallDirectory;
            if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "server.js")))
                throw new InvalidOperationException("未找到 SillyTavern 目录或 server.js，请在设置中选择已有目录。");
            var node = CommandRunner.FindExecutable("node.exe") ?? throw new InvalidOperationException("未找到 Node.js，请检查 PATH。");
            var stamps = ReadConfigStamps(directory);
            // Reading the config costs a whole node -e spawn (0.3-2 s cold). Config files are
            // the only inputs that change between refreshes, so identical stamps reuse the
            // previous result. (Environment overrides are fixed for the manager's lifetime.)
            if (Config is not null && stamps == cachedStamps) return;
            Config = await InstanceConfig.LoadAsync(directory, node);
            cachedStamps = stamps;
            Changed?.Invoke();
        }
        catch
        {
            // Never keep exposing a previous install's port/URL after a failed refresh.
            if (!HasOwnedProcess && Config is not null)
            {
                Config = null;
                cachedStamps = null;
                Changed?.Invoke();
            }
            throw;
        }
        finally { configGate.Release(); }
    }

    private (DateTime Config, DateTime Default, DateTime Package)? cachedStamps;

    private static (DateTime Config, DateTime Default, DateTime Package)? ReadConfigStamps(string directory)
    {
        DateTime Stamp(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
        }
        return (Stamp(Path.Combine(directory, "config.yaml")),
            Stamp(Path.Combine(directory, "default", "config.yaml")),
            Stamp(Path.Combine(directory, "package.json")));
    }

    public static bool PortIsOccupied(int port)
    {
        try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port); }
        catch (NetworkInformationException) { throw new InvalidOperationException("无法检查端口状态，请先排查网络环境。"); }
    }

    /// <summary>
    /// Read-only lookup of the process listening on the port, via the Windows TCP table.
    /// Returns null when the table cannot be read or the process has already exited;
    /// the manager never acts on the process, the answer is display-only.
    /// </summary>
    public static (int ProcessId, string Name)? FindPortOwner(int port)
    {
        try
        {
            foreach (var pid in EnumerateListenerPids(port))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return (pid, process.ProcessName + ".exe");
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return (pid, "未知进程");
                }
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return null;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int TcpRowOwnerPidSize = 24; // DWORD state + 2 sockaddrs (8 B each) + DWORD pid

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool sort, int ipVersion, int tableClass, int reserved);

    private static IEnumerable<int> EnumerateListenerPids(int port)
    {
        var size = 0;
        // First call with a zero buffer returns the required size (ERROR_INSUFFICIENT_BUFFER = 122).
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) yield break;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) yield break;
            var count = Marshal.ReadInt32(buffer);
            var rowBase = buffer + 4; // The table header is a single DWORD entry count.
            for (var i = 0; i < count; i++)
            {
                var row = rowBase + i * TcpRowOwnerPidSize;
                // dwLocalPort is at offset 8 in network byte order (htons).
                var rawPort = Marshal.ReadInt32(row, 8);
                var localPort = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                if (localPort == port) yield return Marshal.ReadInt32(row, 20);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public async Task StartAsync(bool isAutostart = false)
    {
        await lifecycle.WaitAsync();
        try
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
            if (config.SupportsHeartbeat)
            {
                args.Add("--heartbeatInterval");
                args.Add(WatchdogEvaluator.HeartbeatIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            }
            if (isAutostart) { args.Add("--browserLaunchEnabled"); args.Add(settings.OpenBrowserOnAutostart ? "true" : "false"); }
            lock (sync) expectedExit = false;
            SetState(ServerState.Starting, "正在启动，等待服务响应…");
            log.Write("管理器", "正在后台启动 SillyTavern（--listen）。");
            NativeNodeProcess launched;
            try
            {
                launched = NativeNodeProcess.Start(node, settings.InstallDirectory, args, text => log.Write("服务", text));
            }
            catch (Exception ex)
            {
                SetState(ServerState.Failed, "启动失败：" + ex.Message);
                log.Write("管理器", "启动失败：" + ex.Message);
                return;
            }
            lock (sync) child = launched;
            launched.BeginMonitoring(code => OnExit(launched, code));
            _ = ObserveReadinessAsync(launched, config);
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>
    /// One loopback probe client for the whole process lifetime. The permissive certificate
    /// callback is acceptable because this client only ever talks to the configured local endpoint.
    /// </summary>
    private static readonly HttpClient ReadinessClient = new(new HttpClientHandler
    {
        UseProxy = false,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// Probes the loopback URL until it answers. This deliberately does not depend on the
    /// server's startup log text, and keeps polling for slow starts instead of giving up.
    /// </summary>
    private async Task ObserveReadinessAsync(NativeNodeProcess process, InstanceConfig config)
    {
        var started = DateTime.UtcNow;
        var slowNoted = false;
        while (!process.HasExited)
        {
            if (!ReferenceEquals(child, process) || State != ServerState.Starting) return;
            try
            {
                using var response = await ReadinessClient.GetAsync(new Uri(config.LocalUrl));
                if (!ReferenceEquals(child, process) || State != ServerState.Starting) return;
                // Any reply does not mean usable: a broken install answering 5xx on our port
                // must keep probing instead of claiming 运行中.
                var restricted = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                if (response.IsSuccessStatusCode || restricted)
                {
                    SetState(ServerState.Running, restricted ? $"运行中，本机访问受限（{(int)response.StatusCode}）" : "运行中");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            if (!slowNoted && DateTime.UtcNow - started > TimeSpan.FromSeconds(90))
            {
                slowNoted = true;
                SetState(ServerState.Starting, "服务启动较慢，请查看日志");
                Notice?.Invoke("服务启动较慢，请查看日志");
            }
            await Task.Delay(DateTime.UtcNow - started < TimeSpan.FromSeconds(10) ? 300 : 1000);
        }
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
            stopWaitedSeconds = 0;
            if (!wasExpected) lastExitCode = code;
        }
        log.Write("管理器", wasExpected ? $"服务已停止（退出码 {code}）。" : $"服务已退出（退出码 {code}）。");
        SetState(wasExpected ? ServerState.Stopped : ServerState.Failed, wasExpected ? "未运行" : $"服务异常退出（退出码 {code}）");
        // Allow the asynchronous pipe readers to drain their final output before disposal.
        _ = Task.Run(async () => { await Task.Delay(500); exited.Dispose(); });
    }

    /// <summary>
    /// Best-effort synchronous stop for shutdown paths (Windows shutdown/logoff, task-manager
    /// close) that cannot await the async lifecycle. Never throws.
    /// </summary>
    public void EmergencyStopSync()
    {
        NativeNodeProcess? process;
        lock (sync) { process = child; if (process is not null) expectedExit = true; }
        if (process is null || process.HasExited) return;
        log.Write("管理器", "系统正在关机或注销，尽力停止服务。");
        try
        {
            // The interrupt helper completes on thread-pool continuations only, so blocking on
            // it here cannot deadlock. Everything is bounded: shutdown grants just seconds.
            if (!process.SendInterruptAsync().GetAwaiter().GetResult())
                log.Write("管理器", "无法发送正常停止信号。");
        }
        catch (Exception ex) { log.Write("管理器", "无法发送正常停止信号：" + ex.Message); }
        try
        {
            if (!process.WaitForExitSync(TimeSpan.FromSeconds(4))) process.Kill();
        }
        catch (Exception ex) { log.Write("管理器", "关机时停止服务失败：" + ex.Message); }
    }

    public async Task<bool> StopAsync(Func<Task<StopDecision>> askOnTimeout)
    {
        await lifecycle.WaitAsync();
        try
        {
            NativeNodeProcess? process;
            lock (sync) { process = child; if (process is not null) { expectedExit = true; stopWaitedSeconds = 0; } }
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
                lock (sync) stopWaitedSeconds += 8;
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
        finally { lifecycle.Release(); }
    }
}
