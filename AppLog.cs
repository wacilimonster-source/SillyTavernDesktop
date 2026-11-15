using System.Text;
using System.Text.RegularExpressions;

namespace SillyTavernTray;

internal sealed class AppLog
{
    private const long MaximumBytes = 5L * 1024 * 1024;
    private const int MaximumFiles = 10;
    // The live view only needs recent context; one megabyte of the current file covers it
    // without sweeping every rotated file (55 MB worst case) at startup.
    private const long TailScanBytes = 1024 * 1024;
    private readonly object sync = new();
    private static readonly Regex EscapeSequence = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    public string DirectoryPath { get; } = Path.Combine(AppSettings.DataDirectory, "logs");
    private string CurrentPath => Path.Combine(DirectoryPath, "tray.log");
    public event Action<string>? LineWritten;
    private StreamWriter? writer;
    private bool directoryReady;
    private long writtenBytes;

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
                    AppendLine(line);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The live view must remain usable; drop the writer so the next line reopens.
                    writer?.Dispose();
                    writer = null;
                }
            }
            LineWritten?.Invoke(line);
        }
    }

    // A persistent writer replaces the per-line CreateDirectory/Exists/stat/append syscall chain
    // that used to run on every server-output line under this same lock.
    private void AppendLine(string line)
    {
        if (!directoryReady)
        {
            Directory.CreateDirectory(DirectoryPath);
            directoryReady = true;
        }
        if (writer is null)
        {
            // Permissive sharing: crash reports and the update helper append to the same file
            // from other instances/processes while this writer is open.
            var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            writtenBytes = stream.Length;
        }
        writer.WriteLine(line);
        writtenBytes += Encoding.UTF8.GetByteCount(line) + 2; // Environment.NewLine is CRLF on Windows.
        if (writtenBytes < MaximumBytes) return;
        writer.Dispose();
        writer = null;
        Rotate();
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

    /// <summary>Tail of the current log only; the rotated files remain on disk for full history.</summary>
    public IReadOnlyList<string> ReadRecent(int maximumLines = 10000)
    {
        var lines = new Queue<string>();
        lock (sync)
        {
            try
            {
                using var stream = new FileStream(CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > TailScanBytes) stream.Seek(-TailScanBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (stream.Position > 0) reader.ReadLine(); // Skip the line cut in half by the seek.
                while (reader.ReadLine() is { } line)
                {
                    lines.Enqueue(line);
                    if (lines.Count > maximumLines) lines.Dequeue();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return lines.ToArray();
    }
}
