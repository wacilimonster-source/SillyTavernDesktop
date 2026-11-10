using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace SillyTavernTray;

internal sealed class AppSettings
{
    public string InstallDirectory { get; set; } = FindDefaultInstallDirectory();
    public bool StartServerOnLaunch { get; set; } = true;
    public bool OpenBrowserOnAutostart { get; set; }
    public bool AutoUpdateManager { get; set; } = true;
    public bool AutoRestartServer { get; set; } = true;

    /// <summary>Set when a corrupt settings file was moved aside during the last Load.</summary>
    public static string? RecoveryNotice { get; private set; }

    private static string FindDefaultInstallDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "SillyTavern"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "SillyTavern")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitHub", "SillyTavern"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SillyTavern"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SillyTavern"),
            @"C:\SillyTavern"
        };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "server.js"))) ?? "";
    }

    public static string DataDirectory => Environment.GetEnvironmentVariable("ST_TRAY_DATA_DIR") is { Length: > 0 } overridden
        ? Path.GetFullPath(overridden)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SillyTavernTray");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        RecoveryNotice = null;
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException ex)
        {
            // A malformed settings file must not prevent the manager from starting.
            var backup = SettingsPath + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var moved = false;
            try { File.Move(SettingsPath, backup, overwrite: true); moved = true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            RecoveryNotice = moved
                ? $"设置文件无法解析，已备份为 {backup}，本次使用默认设置。"
                : $"设置文件无法解析，且无法备份，本次使用默认设置：{ex.Message}";
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"无法读取管理器设置：{ex.Message}", ex);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SillyTavernTray";

    private static string Command
    {
        get
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定管理器位置。");
            var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            var entry = Path.Combine(AppContext.BaseDirectory, entryName + ".dll");
            return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? $"\"{exe}\" \"{entry}\" --autostart"
                : $"\"{exe}\" --autostart";
        }
    }

    public static bool IsEnabled(string? keyPath = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath ?? RunKey, false);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled, string? keyPath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath ?? RunKey, writable: true)
            ?? throw new InvalidOperationException("无法打开 Windows 登录启动项。");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else if (key.GetValue(ValueName) is string existing &&
                 (string.Equals(existing, Command, StringComparison.OrdinalIgnoreCase) || existing.Contains("SillyTavernTray", StringComparison.OrdinalIgnoreCase)))
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
