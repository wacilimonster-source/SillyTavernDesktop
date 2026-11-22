using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SillyTavernTray;

internal sealed class AppLog
{
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
    private string? currentDate;

    public void Write(string source, string message)
    {
        foreach (var raw in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0) continue;
            var clean = EscapeSequence.Replace(raw, "");
            var now = DateTime.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss}] [{source}] {clean}";
            lock (sync)
            {
                try
                {
                    AppendLine(line, now);
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
    private void AppendLine(string line, DateTime now)
    {
        if (!directoryReady)
        {
            Directory.CreateDirectory(DirectoryPath);
            directoryReady = true;
        }
        var stampDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (currentDate is null)
        {
            // First write since process start: the current file may still hold a previous
            // day (the last session ran before midnight). Archive it so each day starts
            // with a clean file (WinSW-style daily rotation). A restart within the same
            // day keeps appending, so the startup tail still shows the previous session.
            currentDate = stampDate;
            var lastWriteDate = TryGetLastWriteDate(CurrentPath);
            if (lastWriteDate is { } previous && previous != stampDate) RollOver(now);
        }
        else if (stampDate != currentDate)
        {
            currentDate = stampDate;
            if (writer is not null) { writer.Dispose(); writer = null; }
            RollOver(now);
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
        if (writtenBytes < LogRotationPolicy.MaximumBytes) return;
        // Size rotation within the day: close, archive under a time-suffixed name, continue fresh.
        writer.Dispose();
        writer = null;
        RollOver(now);
    }

    /// <summary>
    /// Archives the current log file as a dated file and lets the next write start a fresh
    /// one. The writer must already be closed. A locked file (a helper still holds it)
    /// propagates the IOException to the caller's catch, which reopens the current file;
    /// rotation then retries on a later line.
    /// </summary>
    private void RollOver(DateTime now)
    {
        if (writer is not null) throw new InvalidOperationException("RollOver requires a closed writer.");
        if (File.Exists(CurrentPath))
        {
            var archive = Path.Combine(DirectoryPath, LogRotationPolicy.ResolveArchiveName(now, ListArchives()));
            File.Move(CurrentPath, archive, overwrite: false);
            writtenBytes = 0;
        }
        // Enumerate on disk so retention only ever touches real archive files.
        foreach (var name in LogRotationPolicy.ExcessArchives(ListArchives()))
        {
            try { File.Delete(Path.Combine(DirectoryPath, name)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string? TryGetLastWriteDate(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return File.GetLastWriteTime(path).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private List<string> ListArchives()
    {
        var names = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "tray*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (name != "tray.log") names.Add(name); // tray-*.log archives and legacy tray.log.N backups.
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>Tail of the current log only; the archived files remain on disk for full history.</summary>
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
