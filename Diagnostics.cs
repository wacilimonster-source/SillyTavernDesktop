using System.Text;
using System.Text.RegularExpressions;

namespace SillyTavernTray;

/// <summary>
/// Builds the copyable diagnostics block (v2 §4.8). Deliberately minimal exposure:
/// loopback URL only, no LAN addresses, no secrets, no full log files.
/// </summary>
internal static class Diagnostics
{
    private static readonly Regex PrivateAddress = new(
        @"(?<![\d.])(?:192\.168\.\d{1,3}\.\d{1,3}|10\.\d{1,3}\.\d{1,3}\.\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})(?![\d.])",
        RegexOptions.Compiled);

    public static int CrashReportCount
    {
        get
        {
            try { return Directory.GetFiles(AppSettings.DataDirectory, "crash-*.txt").Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    public static string Build(
        string serverStateLine,
        InstanceConfig? config,
        string branch,
        string? securityLine,
        string? extraLine,
        bool watchdogEnabled,
        int watchdogRestarts,
        string? watchdogLastTime,
        (string Kind, DateTime Time)? lastMaintenance,
        IReadOnlyList<string> recentLogLines)
    {
        var builder = new StringBuilder();
        builder.AppendLine("── SillyTavern 管理器诊断 ──");
        builder.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine("管理器：v" + ManagerVersion.Current);
        builder.AppendLine("服务状态：" + serverStateLine);
        builder.AppendLine(config is null
            ? "SillyTavern：未读取配置"
            : $"SillyTavern：{config.Version} · 分支 {branch} · 端口 {config.Port} · {config.LocalUrl}");
        builder.AppendLine("访问安全：" + (securityLine ?? "未知"));
        builder.AppendLine(watchdogEnabled
            ? "看门狗：已启用 · 本次会话自动重启 " + watchdogRestarts + " 次" +
              (watchdogLastTime is null ? "" : "（最近 " + watchdogLastTime + "）")
            : "看门狗：已关闭");
        if (lastMaintenance is { } maintenance)
            builder.AppendLine($"最近维护：{maintenance.Kind} {maintenance.Time:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrEmpty(extraLine)) builder.AppendLine(extraLine);
        builder.AppendLine("崩溃报告：" + CrashReportCount + " 份");
        builder.AppendLine("日志目录：" + new AppLog().DirectoryPath);
        builder.AppendLine("── 最近日志（" + Math.Min(recentLogLines.Count, 30) + " 行）──");
        foreach (var line in recentLogLines.TakeLast(30)) builder.AppendLine(Scrub(line));
        return builder.ToString();
    }

    /// <summary>Loopback listeners are fine; LAN addresses never leave the machine via diagnostics.</summary>
    public static string Scrub(string text) => PrivateAddress.Replace(text, "[已隐去局域网地址]");
}
