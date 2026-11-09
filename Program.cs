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
