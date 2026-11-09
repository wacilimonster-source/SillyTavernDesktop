using System.Text;
using System.Text.RegularExpressions;

namespace SillyTavernTray;

internal sealed class AppLog
{
    private const long MaximumBytes = 5L * 1024 * 1024;
    private const int MaximumFiles = 10;
    private readonly object sync = new();
    private static readonly Regex EscapeSequence = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    public string DirectoryPath { get; } = Path.Combine(AppSettings.DataDirectory, "logs");
    private string CurrentPath => Path.Combine(DirectoryPath, "tray.log");
    public event Action<string>? LineWritten;

    public void Write(string source, string message)
    {
        foreach (var raw in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0) continue;
            var clean = EscapeSequence.Replace(raw, "");
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {clean}";
            lock (sync)
            {
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length >= MaximumBytes) Rotate();
                    File.AppendAllText(CurrentPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* The live view must remain usable. */ }
            }
            LineWritten?.Invoke(line);
        }
    }

    private void Rotate()
    {
        var oldest = CurrentPath + "." + (MaximumFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = MaximumFiles - 2; i >= 1; i--)
        {
            var previous = CurrentPath + "." + i;
            if (File.Exists(previous)) File.Move(previous, CurrentPath + "." + (i + 1), true);
        }
        File.Move(CurrentPath, CurrentPath + ".1", true);
    }

    public IReadOnlyList<string> ReadRecent(int maximumLines = 10000)
    {
        var lines = new Queue<string>();
        lock (sync)
        {
            for (var i = MaximumFiles - 1; i >= 0; i--)
            {
                var path = i == 0 ? CurrentPath : CurrentPath + "." + i;
                if (!File.Exists(path)) continue;
                try
                {
                    foreach (var line in File.ReadLines(path, Encoding.UTF8))
                    {
                        lines.Enqueue(line);
                        if (lines.Count > maximumLines) lines.Dequeue();
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return lines.ToArray();
    }
}
