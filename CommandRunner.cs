using System.Diagnostics;
using System.Text;

namespace SillyTavernTray;

internal sealed record CommandResult(int ExitCode, string Output, string Error);

internal static class CommandRunner
{
    public static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return null;
    }

    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, string directory,
        Action<string>? onOutput = null, bool production = false, string? rawArguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (rawArguments is not null) info.Arguments = rawArguments;
        else foreach (var argument in args) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "Never";
        if (production) info.Environment["NODE_ENV"] = "production";
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
        process.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        async Task Pump(StreamReader reader, StringBuilder builder)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (builder.Length < 1024 * 1024) builder.AppendLine(line);
                onOutput?.Invoke(line);
            }
        }
        var outTask = Pump(process.StandardOutput, output);
        var errorTask = Pump(process.StandardError, error);
        await process.WaitForExitAsync();
        await Task.WhenAll(outTask, errorTask);
        return new CommandResult(process.ExitCode, output.ToString(), error.ToString());
    }

    public static Task<CommandResult> RunNpmInstallAsync(string npm, string directory, Action<string> onOutput,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        const string args = "install --no-save --no-audit --no-fund --loglevel=error --no-progress --omit=dev --ignore-scripts";
        return RunNpmAsync(npm, directory, args, onOutput, production: true, environment);
    }

    public static Task<CommandResult> GetNpmRegistryAsync(string npm, string directory) =>
        RunNpmAsync(npm, directory, "config get registry", null, production: false, environment: null);

    private static Task<CommandResult> RunNpmAsync(string npm, string directory, string arguments, Action<string>? onOutput,
        bool production, IReadOnlyDictionary<string, string>? environment)
    {
        // The arguments are fixed by this program. The path is expanded once from an environment variable by cmd.exe.
        var command = $"/d /s /c \"\"%ST_TRAY_NPM_PATH%\" {arguments}\"";
        var childEnvironment = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        childEnvironment["ST_TRAY_NPM_PATH"] = npm;
        return RunAsync(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", [], directory, onOutput, production: production,
            rawArguments: command, environment: childEnvironment);
    }
}
