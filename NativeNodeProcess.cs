using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SillyTavernTray;

/// <summary>A private hidden console permits the server to receive a real Ctrl+C on Windows.</summary>
internal sealed class NativeNodeProcess : IDisposable
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 1;
    private readonly Process process;
    private readonly SafeFileHandle inputWrite;
    private readonly StreamReader stdout;
    private readonly StreamReader stderr;
    private readonly Action<string> line;
    private readonly TaskCompletionSource monitoredExit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NativeNodeProcess(Process process, SafeFileHandle inputWrite, SafeFileHandle outputRead,
        SafeFileHandle errorRead, Action<string> line)
    {
        this.process = process;
        this.inputWrite = inputWrite;
        this.line = line;
        stdout = new StreamReader(new FileStream(outputRead, FileAccess.Read), new UTF8Encoding(false, false), true);
        stderr = new StreamReader(new FileStream(errorRead, FileAccess.Read), new UTF8Encoding(false, false), true);
    }

    public int Id => process.Id;
    public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

    public static NativeNodeProcess Start(string nodePath, string workingDirectory, IReadOnlyList<string> args, Action<string> line)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null, inRead = null, inWrite = null;
        try
        {
            if (!CreatePipe(out outRead, out outWrite, ref security, 0) ||
                !CreatePipe(out errRead, out errWrite, ref security, 0) ||
                !CreatePipe(out inRead, out inWrite, ref security, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetHandleInformation(outRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(errRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(inWrite, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(), Flags = StartfUseShowWindow | StartfUseStdHandles,
                ShowWindow = 0, StdInput = inRead.DangerousGetHandle(),
                StdOutput = outWrite.DangerousGetHandle(), StdError = errWrite.DangerousGetHandle()
            };
            var command = new StringBuilder(string.Join(" ", new[] { nodePath }.Concat(args).Select(QuoteArgument)));
            if (!CreateProcess(nodePath, command, IntPtr.Zero, IntPtr.Zero, true, CreateNewConsole,
                    IntPtr.Zero, workingDirectory, ref startup, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var child = Process.GetProcessById((int)info.ProcessId);
                var result = new NativeNodeProcess(child, inWrite, outRead, errRead, line);
                inWrite = outRead = errRead = null;
                return result;
            }
            finally { CloseHandle(info.Process); CloseHandle(info.Thread); }
        }
        finally
        {
            outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose(); inRead?.Dispose(); inWrite?.Dispose();
        }
    }

    public void BeginMonitoring(Action<int> exited)
    {
        _ = PumpAsync(stdout);
        _ = PumpAsync(stderr);
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(); exited(process.ExitCode); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { exited(-1); }
            finally { monitoredExit.TrySetResult(); }
        });
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } text) line(text);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public async Task<bool> SendInterruptAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定管理器程序路径。");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, System.Reflection.Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        info.ArgumentList.Add("--signal-console");
        info.ArgumentList.Add(Id.ToString());
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("无法启动停止服务所需的辅助进程。");
        await helper.WaitForExitAsync();
        return helper.ExitCode == 0;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
            await monitoredExit.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return true;
        }
        catch (TimeoutException) { return HasExited; }
    }

    public void Kill()
    {
        if (!HasExited) process.Kill(entireProcessTree: true);
    }

    public void Dispose()
    {
        inputWrite.Dispose();
        stdout.Dispose();
        stderr.Dispose();
        process.Dispose();
    }

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags; public short ShowWindow, Reserved2; public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation
    {
        public IntPtr Process, Thread; public uint ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
