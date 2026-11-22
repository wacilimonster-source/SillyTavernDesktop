using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;

namespace SillyTavernTray;

// Log panel pipeline: queueing, filtering, search and the RTF batch renderer.
internal sealed partial class MainForm
{
    private readonly ConcurrentQueue<string> queuedLines = new();
    private readonly List<string> logLines = new();
    private readonly System.Windows.Forms.Timer logTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer logSearchTimer = new() { Interval = 220 };
    private int omittedLiveLines;
    private readonly List<(string Source, bool IsSeparator)> renderedEntries = [];
    private string? lastRenderedDate;
    private readonly List<(int Offset, int Length)> searchMatches = [];
    private int searchMatchIndex = -1;
    private SearchPopup? searchPopup;
    private bool pausedScroll;

    private void OnLogLine(string line)
    {
        queuedLines.Enqueue(line.Length > 8192 ? line[..8192] + "…（完整内容见日志文件）" : line);
        while (queuedLines.Count > 12000 && queuedLines.TryDequeue(out _)) Interlocked.Increment(ref omittedLiveLines);
    }

    private bool Matches(string line)
    {
        var source = logSources.SelectedIndex > 0 && logSources.SelectedIndex < logSources.Items.Length ? logSources.Items[logSources.SelectedIndex] : "全部";
        return (source == "全部" || line.Contains($"] [{source}] ", StringComparison.Ordinal))
            && (logSearch.Text.Length == 0 || line.Contains(logSearch.Text, StringComparison.CurrentCultureIgnoreCase));
    }

    private void FlushLogQueue()
    {
        var render = !hiddenToTray && !pausedScroll;
        var omitted = Interlocked.Exchange(ref omittedLiveLines, 0);
        if (omitted > 0)
        {
            var notice = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 实时窗口省略 {omitted} 条日志，完整记录请查看日志文件。";
            logLines.Add(notice);
            if (render && Matches(notice)) AppendColored(notice);
        }
        var appended = false;
        var matchedAppended = false;
        var searching = logSearch.Text.Length > 0;
        var budget = render ? Stopwatch.StartNew() : null;
        var limit = render ? 400 : 4000;
        var processed = 0;
        if (render) SuspendLogRedraw();
        try
        {
            while (processed < limit && (budget is null || budget.ElapsedMilliseconds < 40)
                   && queuedLines.TryDequeue(out var line))
            {
                logLines.Add(line);
                if (logLines.Count > 10000) logLines.RemoveRange(0, Math.Min(500, logLines.Count - 10000 + 499));
                if (render && Matches(line))
                {
                    AppendColored(line);
                    appended = true;
                    if (searching) matchedAppended = true;
                }
                processed++;
            }
        }
        finally { if (render) ResumeLogRedraw(); }
        if (!appended) return;
        // While searching, appended matches must be highlighted and counted; a rebuild is cheap enough.
        if (matchedAppended)
        {
            RebuildLog();
            return;
        }
        logView.SelectionStart = logView.TextLength;
        logView.SelectionLength = 0;
        logView.ScrollToCaret();
    }

    private void RebuildLog()
    {
        if (logView.IsDisposed) return;
        var visible = logLines.Where(Matches).ToArray();
        var truncated = visible.Length > 800;
        if (truncated) visible = visible[^800..];
        SuspendLogRedraw();
        logView.SuspendLayout();
        try
        {
            logView.Clear();
            renderedEntries.Clear();
            lastRenderedDate = null;
            var batch = visible.ToList();
            if (truncated)
                batch.Insert(0, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [管理器] 仅显示最近 800 条匹配日志，完整记录见日志文件。");
            AppendColoredBatch(batch);
            if (!pausedScroll)
            {
                logView.SelectionStart = logView.TextLength;
                logView.SelectionLength = 0;
                logView.ScrollToCaret();
            }
            ApplySearchHighlights();
        }
        finally
        {
            logView.ResumeLayout();
            ResumeLogRedraw();
        }
        UpdateLogCount();
    }

    private static string DateSeparatorText(DateTime stamp) =>
        $"──────────── {stamp.Month} 月 {stamp.Day} 日 ────────────";

    private void AppendDateSeparatorIfNeeded(DateTime stamp)
    {
        var date = stamp.ToString("yyyy-MM-dd");
        if (date == lastRenderedDate) return;
        lastRenderedDate = date;
        var separator = DateSeparatorText(stamp);
        renderedEntries.Add((separator, true));
        logView.SelectionStart = logView.TextLength;
        logView.SelectionLength = 0;
        logView.SelectionColor = Theme.Current.TextTertiary;
        logView.AppendText(separator + Environment.NewLine);
    }

    private void AppendColored(string line)
    {
        var p = Theme.Current;
        var start = logView.TextLength;
        logView.SelectionStart = start;
        logView.SelectionLength = 0;
        if (!TryParseLogLine(line, out var stamp, out var time, out var source, out var message))
        {
            renderedEntries.Add((line, false));
            logView.SelectionColor = p.TextPrimary;
            logView.AppendText(line + Environment.NewLine);
            return;
        }
        AppendDateSeparatorIfNeeded(stamp);
        renderedEntries.Add((line, false));
        logView.SelectionColor = p.TextTertiary;
        logView.AppendText(time + "  ");
        logView.SelectionColor = source switch
        {
            "服务" => p.Accent,
            "维护" => p.Purple,
            _ => p.TextSecondary
        };
        logView.AppendText(source + "  ");
        logView.SelectionColor = p.TextPrimary;
        logView.AppendText(message + Environment.NewLine);
    }

    /// <summary>Inserts a whole rebuild batch with one native RTF call instead of per-line appends.</summary>
    private void AppendColoredBatch(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var p = Theme.Current;
        var fontSize = Math.Max(1, (int)Math.Round(Typography.Mono.SizeInPoints * 2));
        var rtf = new StringBuilder();
        rtf.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil\fcharset0 ").Append(Typography.Mono.FontFamily.Name).Append(@";}}");
        rtf.Append(@"{\colortbl ;");
        AppendRtfColor(rtf, p.TextTertiary);
        AppendRtfColor(rtf, p.Accent);
        AppendRtfColor(rtf, p.Purple);
        AppendRtfColor(rtf, p.TextSecondary);
        AppendRtfColor(rtf, p.TextPrimary);
        rtf.Append(@"}\viewkind4\uc1\pard\f0\fs").Append(fontSize.ToString(CultureInfo.InvariantCulture)).Append(' ');
        foreach (var line in lines)
        {
            if (!TryParseLogLine(line, out var stamp, out var time, out var source, out var message))
            {
                renderedEntries.Add((line, false));
                rtf.Append(@"\cf5 ");
                AppendRtfText(rtf, line);
                rtf.Append(@"\par").Append('\n');
                continue;
            }
            var date = stamp.ToString("yyyy-MM-dd");
            if (date != lastRenderedDate)
            {
                lastRenderedDate = date;
                var separator = DateSeparatorText(stamp);
                renderedEntries.Add((separator, true));
                rtf.Append(@"\cf1 ");
                AppendRtfText(rtf, separator);
                rtf.Append(@"\par").Append('\n');
            }
            renderedEntries.Add((line, false));
            rtf.Append(@"\cf1 ");
            AppendRtfText(rtf, time + "  ");
            rtf.Append(@"\cf").Append(source switch { "服务" => '2', "维护" => '3', _ => '4' }).Append(' ');
            AppendRtfText(rtf, source + "  ");
            rtf.Append(@"\cf5 ");
            AppendRtfText(rtf, message);
            rtf.Append(@"\par").Append('\n');
        }
        rtf.Append('}');
        logView.SelectedRtf = rtf.ToString();
    }

    private static void AppendRtfColor(StringBuilder builder, Color color) =>
        builder.Append(@"\red").Append(color.R.ToString(CultureInfo.InvariantCulture))
            .Append(@"\green").Append(color.G.ToString(CultureInfo.InvariantCulture))
            .Append(@"\blue").Append(color.B.ToString(CultureInfo.InvariantCulture)).Append(';');

    private static void AppendRtfText(StringBuilder builder, string text)
    {
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\r': break;
                case '\n': builder.Append(@"\par").Append('\n'); break;
                default:
                    if (ch < 128) builder.Append(ch);
                    else builder.Append(@"\u").Append(((short)ch).ToString(CultureInfo.InvariantCulture)).Append('?');
                    break;
            }
        }
    }

    private static bool TryParseLogLine(string line, out DateTime stamp, out string time, out string source, out string message)
    {
        stamp = DateTime.MinValue;
        time = source = message = "";
        if (line.Length < 22 || line[0] != '[') return false;
        var close = line.IndexOf(']');
        if (close < 0) return false;
        if (!DateTime.TryParseExact(line[1..close], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return false;
        var rest = line[(close + 1)..].TrimStart();
        if (rest.Length < 3 || rest[0] != '[') return false;
        var close2 = rest.IndexOf(']');
        if (close2 < 0) return false;
        // The date itself comes from the separator rows; lines only carry the clock time.
        stamp = timestamp;
        time = timestamp.ToString("HH:mm:ss");
        source = rest[1..close2];
        message = rest[(close2 + 1)..].TrimStart();
        return true;
    }

    private void TogglePause()
    {
        if (pausedScroll) ResumeScroll(); else PauseScroll();
    }

    private void PauseScroll()
    {
        if (pausedScroll) return;
        pausedScroll = true;
        backToBottom.Visible = true;
        pauseButton.Glyph = Icons.Play;
        pauseButton.AccessibleName = "继续滚动";
        toolTip.SetToolTip(pauseButton, "继续滚动");
        PositionBackToBottom();
    }

    private void ResumeScroll()
    {
        pausedScroll = false;
        backToBottom.Visible = false;
        pauseButton.Glyph = Icons.Pause;
        pauseButton.AccessibleName = "暂停滚动";
        toolTip.SetToolTip(pauseButton, "暂停滚动");
        RebuildLog();
    }

    private void PositionBackToBottom()
    {
        if (logSurface is null) return;
        backToBottom.Location = new Point(
            Math.Max(S(8), logSurface.ClientSize.Width - backToBottom.Width - S(12)),
            Math.Max(S(8), logSurface.ClientSize.Height - backToBottom.Height - S(12)));
    }

    private void OpenSearchPopup()
    {
        if (searchPopup is { IsDisposed: false })
        {
            searchPopup.Box.FocusInput();
            return;
        }
        searchPopup = new SearchPopup { Box = { Text = logSearch.Text } };
        searchPopup.Box.TextChanged += (_, _) => { if (searchPopup is { IsDisposed: false }) logSearch.Text = searchPopup.Box.Text; };
        searchPopup.Box.NavigateRequested += forward => NavigateSearch(forward ? 1 : -1);
        var anchor = searchToggleButton.PointToScreen(new Point(0, searchToggleButton.Height + S(4)));
        var area = Screen.FromControl(this).WorkingArea;
        searchPopup.Location = new Point(
            Math.Min(Math.Max(area.Left + S(8), anchor.X - S(80)), area.Right - searchPopup.Width - S(8)),
            Math.Min(anchor.Y, area.Bottom - searchPopup.Height - S(8)));
        searchPopup.Show(this);
        searchPopup.Box.FocusInput();
    }

    private void NavigateSearch(int direction)
    {
        if (searchMatches.Count == 0) return;
        searchMatchIndex = ((searchMatchIndex + direction) % searchMatches.Count + searchMatches.Count) % searchMatches.Count;
        var (offset, length) = searchMatches[searchMatchIndex];
        logView.SelectionStart = offset;
        logView.SelectionLength = length;
        logView.ScrollToCaret();
        UpdateLogCount();
    }

    private void ApplySearchHighlights()
    {
        searchMatches.Clear();
        searchMatchIndex = -1;
        var query = logSearch.Text.Trim();
        if (query.Length == 0) return;
        var text = logView.Text;
        var index = 0;
        while ((index = text.IndexOf(query, index, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            searchMatches.Add((index, query.Length));
            index += query.Length;
        }
        if (searchMatches.Count == 0) return;
        foreach (var (offset, length) in searchMatches)
        {
            logView.SelectionStart = offset;
            logView.SelectionLength = length;
            logView.SelectionBackColor = Theme.Current.AccentSoft;
            logView.SelectionColor = Theme.Current.TextPrimary;
            logView.SelectionFont = Typography.MonoBold;
        }
        // Land on the newest match so it is on screen without scrolling.
        searchMatchIndex = searchMatches.Count - 1;
        var (activeOffset, activeLength) = searchMatches[searchMatchIndex];
        logView.SelectionStart = activeOffset;
        logView.SelectionLength = activeLength;
        logView.ScrollToCaret();
    }

    private void UpdateLogCount()
    {
        var query = logSearch.Text.Trim();
        if (query.Length > 0)
        {
            logCount.Visible = true;
            logCount.Text = searchMatches.Count == 0
                ? "0 处"
                : searchMatchIndex >= 0 ? $"{searchMatchIndex + 1} / {searchMatches.Count}" : $"{searchMatches.Count} 处";
            return;
        }
        var filtering = logSources.SelectedIndex > 0;
        logCount.Visible = filtering;
        if (filtering) logCount.Text = $"{logLines.Count(Matches)} 条";
    }

    private void SuspendLogRedraw()
    {
        if (logView.IsHandleCreated) SendMessage(logView.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
    }

    private void ResumeLogRedraw()
    {
        if (!logView.IsHandleCreated) return;
        SendMessage(logView.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
        logView.Invalidate();
    }

    /// <summary>Floating search input for the compact (600–679) layout (v2 §4.10).</summary>
    private sealed class SearchPopup : ThemedForm
    {
        public SearchPopup() : base(resizable: false)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(260, 42);
            Padding = new Padding(6);
            BackColor = Theme.Current.Surface;
            Box.Dock = DockStyle.Fill;
            Box.Margin = Fill;
            Controls.Add(Box);
            Deactivate += (_, _) => Close();
        }

        public SearchBox Box { get; } = new();
    }
}
