using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SillyTavernTray;

internal static class Program
{
    private static string InstanceSuffix
    {
        get
        {
            var data = Environment.GetEnvironmentVariable("ST_TRAY_DATA_DIR");
            return string.IsNullOrWhiteSpace(data) ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToUpperInvariant())))[..16];
        }
    }
    internal static string InstanceName => @"Local\SillyTavernTray.Instance" + InstanceSuffix;
    internal static string ShowEventName => @"Local\SillyTavernTray.ShowWindow" + InstanceSuffix;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--signal-console" && uint.TryParse(args[1], out var pid))
            return ConsoleSignal.SendInterrupt(pid) ? 0 : 1;
        if (args.Length > 0 && args[0] == ManagerUpdateInstaller.HelperArgument)
            return ManagerUpdateInstaller.RunHelper(args);

        using var mutex = new Mutex(initiallyOwned: true, InstanceName, out var created);
        if (!created)
        {
            try { using var signal = EventWaitHandle.OpenExisting(ShowEventName); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        if (!args.Contains(ManagerUpdateInstaller.SkipArgument) && ManagerUpdateInstaller.TryLaunch(restart: true, autostart: args.Contains("--autostart")))
            return 0;

        PendingManagerUpdateStore.CleanupStaleFiles();

        ApplicationConfiguration.Initialize();
        CrashReporter.Install();
        try
        {
            using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            using var form = new MainForm(args.Contains("--autostart"), showSignal);
            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "startup-error.txt"), ex.ToString());
            }
            catch { }
            MessageBox.Show(ex.Message, "SillyTavern 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

/// <summary>Unlogged crashes made every other diagnosis impossible; this keeps a trace on disk.</summary>
internal static class CrashReporter
{
    private static int fatalReported;

    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report("UI 线程", e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("后台线程", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()), fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => Report("未观察任务", e.Exception, fatal: false);
    }

    private static void Report(string origin, Exception exception, bool fatal)
    {
        var stamp = DateTime.Now;
        var text = $"[{stamp:yyyy-MM-dd HH:mm:ss}] [{origin}]{(fatal ? "（致命，进程即将退出）" : "")}\n{exception}";
        try { new AppLog().Write("崩溃", text.ReplaceLineEndings(" | ")); } catch { }
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(Path.Combine(AppSettings.DataDirectory, "crash-" + stamp.ToString("yyyyMMdd-HHmmssff") + ".txt"), text);
            PruneOldReports();
        }
        catch { }
        if (fatal && Interlocked.Exchange(ref fatalReported, 1) == 0)
            MessageBox.Show("发生未处理的异常，SillyTavern 管理器即将退出。\n详情见数据目录中的崩溃报告。\n\n" + exception.Message,
                "SillyTavern 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void PruneOldReports()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(AppSettings.DataDirectory, "crash-*.txt")
                         .OrderByDescending(File.GetLastWriteTime).Skip(10))
                File.Delete(path);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class ConsoleSignal
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    public static bool SendInterrupt(uint pid)
    {
        // This helper has no UI and must detach from any console it inherited before joining
        // the dedicated server console. The GUI process itself is never attached.
        FreeConsole();
        if (!AttachConsole(pid)) return false;
        try
        {
            if (!SetConsoleCtrlHandler(IntPtr.Zero, true)) return false;
            var result = GenerateConsoleCtrlEvent(0, 0);
            Thread.Sleep(200);
            return result;
        }
        finally { FreeConsole(); }
    }
}
