using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SillyTavernTray.Ui;
using SillyTavernTray.Ui.Controls;

namespace SillyTavernTray;

// Direct user actions: service start/stop/restart entry points, address picking
// and the clipboard actions (addresses, log, diagnostics).
internal sealed partial class MainForm
{
    private readonly List<AddressItem> addressItems = new();
    private int selectedAddress;
    private InstanceConfig? lastAddressConfig;
    private DateTime lastAddressRefreshUtc = DateTime.MinValue;

    private async Task StartOrStopAsync() => await RunExclusiveAsync(
        server.HasOwnedProcess ? BusyOperation.Stopping : BusyOperation.Starting,
        async () =>
        {
            ClearWatchdogBadge();
            if (server.HasOwnedProcess) await server.StopAsync(AskStopDecisionAsync);
            else
            {
                var branchTask = maintenance.GetBranchAsync();
                await server.StartAsync();
                branch = await branchTask;
            }
        });

    private async Task RestartAsync()
    {
        ClearWatchdogBadge();
        if (server.HasOwnedProcess)
        {
            var stopped = false;
            await RunExclusiveAsync(BusyOperation.Stopping, async () => { stopped = await server.StopAsync(AskStopDecisionAsync); });
            if (!stopped) return;
        }
        await RunExclusiveAsync(BusyOperation.Starting, async () =>
        {
            var branchTask = maintenance.GetBranchAsync();
            await server.StartAsync();
            branch = await branchTask;
        });
    }

    private async Task RunPrimaryAsync()
    {
        switch (primaryKind)
        {
            case PrimaryActionKind.OpenServer: OpenServer(); break;
            case PrimaryActionKind.Start: await StartOrStopAsync(); break;
            case PrimaryActionKind.Retry:
                // In the occupied state "retry" must re-check the port and start when it is free.
                if (server.State == ServerState.Occupied) await StartOrStopAsync();
                else await RefreshEnvironmentAsync();
                break;
            case PrimaryActionKind.ChooseDirectory: await ChooseInstallDirectoryAsync(); break;
        }
    }

    private async Task ChooseInstallDirectoryAsync()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 server.js 的 SillyTavern 目录",
            SelectedPath = Directory.Exists(settings.InstallDirectory) ? settings.InstallDirectory : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = Path.GetFullPath(dialog.SelectedPath.Trim());
        if (!File.Exists(Path.Combine(path, "server.js")))
        {
            ShowToast("该目录中没有 server.js，请选择已有的 SillyTavern 目录。", ToastKind.Warning);
            return;
        }
        var candidate = new AppSettings
        {
            InstallDirectory = path,
            StartServerOnLaunch = settings.StartServerOnLaunch,
            OpenBrowserOnAutostart = settings.OpenBrowserOnAutostart,
            AutoUpdateManager = settings.AutoUpdateManager,
            AutoRestartServer = settings.AutoRestartServer,
            Appearance = settings.Appearance
        };
        try { candidate.Save(); }
        catch (Exception ex) { ShowToast("无法保存设置：" + ex.Message, ToastKind.Error); return; }
        settings.InstallDirectory = candidate.InstallDirectory;
        configReadFailed = false;
        await RefreshEnvironmentAsync();
    }

    private async Task HandleActionAsync(ViewActionKind kind)
    {
        switch (kind)
        {
            case ViewActionKind.Stop: await StartOrStopAsync(); break;
            case ViewActionKind.Restart: await RestartAsync(); break;
            case ViewActionKind.OpenServer: OpenServer(); break;
            case ViewActionKind.OpenSettings: ShowSettings(); break;
            case ViewActionKind.ViewLog: RestoreWindow(); ResumeScroll(); break;
            case ViewActionKind.CopyDiagnostics: CopyDiagnostics(); break;
        }
    }

    private void PopulateAddresses()
    {
        var config = server.Config;
        if (config is null) { addressItems.Clear(); lastAddressConfig = null; return; }
        // Enumerating network adapters is a slow IP-helper call and UpdateDisplay runs on every
        // state change, so only re-enumerate for a new config or after a refresh interval.
        if (ReferenceEquals(config, lastAddressConfig) && DateTime.UtcNow - lastAddressRefreshUtc <= TimeSpan.FromSeconds(10)) return;
        lastAddressConfig = config;
        lastAddressRefreshUtc = DateTime.UtcNow;
        var selected = addressItems.Count > 0 && selectedAddress < addressItems.Count ? addressItems[selectedAddress].Url : null;
        var items = new List<AddressItem> { new("本机", config.LocalUrl) };
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { config.LocalUrl };
        if (config.Ipv4 || config.Ipv6)
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                             n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    try
                    {
                        var addresses = nic.GetIPProperties().UnicastAddresses;
                        if (config.Ipv4)
                        {
                            foreach (var address in addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                            {
                                if (IPAddress.IsLoopback(address.Address) ||
                                    address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)) continue;
                                var url = $"{(config.Ssl ? "https" : "http")}://{address.Address}:{config.Port}/";
                                if (urls.Add(url)) items.Add(new AddressItem(nic.Name, url));
                            }
                        }
                        if (config.Ipv6)
                        {
                            foreach (var address in addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6))
                            {
                                if (IPAddress.IsLoopback(address.Address) || address.Address.IsIPv6LinkLocal ||
                                    address.Address.IsIPv6Multicast) continue;
                                var url = $"{(config.Ssl ? "https" : "http")}://[{address.Address}]:{config.Port}/";
                                if (urls.Add(url)) items.Add(new AddressItem(nic.Name, url));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException) { }
        }
        if (addressItems.Select(x => x.Url).SequenceEqual(items.Select(x => x.Url))) return;
        addressItems.Clear();
        addressItems.AddRange(items);
        selectedAddress = Math.Max(0, items.FindIndex(x => x.Url == selected));
        UpdateAddressText();
    }

    private void UpdateAddressText()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0) addressPicker.SelectedText = "";
        else
        {
            selectedAddress = Math.Clamp(selectedAddress, 0, addressItems.Count - 1);
            var item = addressItems[selectedAddress];
            addressPicker.SelectedText = $"{item.Name} · {item.Url}";
        }
        addressPicker.Invalidate();
    }

    private void ShowAddressMenu()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0) return;
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            Font = Typography.Body,
            DropShadowEnabled = true
        };
        for (var i = 0; i < addressItems.Count; i++)
        {
            var index = i;
            var item = new ToolStripMenuItem($"{addressItems[i].Name} · {addressItems[i].Url}");
            item.Click += (_, _) => { selectedAddress = index; UpdateAddressText(); };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => Dwm.Apply(menu.Handle);
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(addressPicker, new Point(0, addressPicker.Height));
    }

    private sealed record AddressItem(string Name, string Url);

    private async void CopyAddress()
    {
        if (!addressPicker.Enabled || addressItems.Count == 0 || selectedAddress >= addressItems.Count) return;
        try { Clipboard.SetText(addressItems[selectedAddress].Url); } catch (ExternalException) { return; }
        var original = copyAddressButton.IconGlyph;
        copyAddressButton.IconGlyph = Icons.Check;
        copyAddressButton.IconColorOverride = Theme.Current.SuccessText;
        copyAddressButton.Invalidate();
        await Task.Delay(1500);
        if (IsDisposed || copyAddressButton.IsDisposed) return;
        copyAddressButton.IconGlyph = original;
        copyAddressButton.IconColorOverride = null;
        copyAddressButton.Invalidate();
    }

    private async void CopyLog()
    {
        if (logView.SelectionLength > 0)
        {
            var value = SelectedLogText();
            if (string.IsNullOrEmpty(value)) return;
            try { Clipboard.SetText(value); } catch (ExternalException) { return; }
            await FlashCheck(copyLogButton);
            return;
        }
        // No selection: copy every currently visible line with full timestamps (v2 §4.7).
        var lines = renderedEntries.Where(entry => !entry.IsSeparator).Select(entry => entry.Source).ToList();
        if (lines.Count == 0) return;
        try { Clipboard.SetText(string.Join("\r\n", lines)); } catch (ExternalException) { return; }
        ShowToast($"已复制 {lines.Count} 行日志（含完整时间戳）", ToastKind.Success);
        await FlashCheck(copyLogButton);
    }

    private async void CopyDiagnostics()
    {
        var config = server.Config;
        var state = server.State;
        string stateLine;
        if (state == ServerState.Running && runningSinceUtc is { } since)
            stateLine = $"运行中（已运行 {FormatDuration(DateTime.UtcNow - since)}）";
        else if (state == ServerState.Failed)
            stateLine = "服务异常退出" +
                (server.LastExitCode is { } code ? $"（退出码 {code}）" : "") +
                (lastRunDuration is { } duration ? $"，退出前已运行 {FormatDuration(duration)}" : "");
        else if (busyOperation != BusyOperation.None)
            stateLine = AppStateFactory.BusyTitle(busyOperation).TrimEnd('…');
        else stateLine = server.Detail;
        var security = config is null ? null : $"白名单{(config.Whitelist ? "开" : "关")} · 认证{(config.BasicAuth || config.UserAccounts ? "开" : "未开")}";
        string? extra = null;
        if (state == ServerState.Occupied && config is not null && QueryOccupier(config.Port) is { } owner)
            extra = $"占用进程：{owner.Name} (PID {owner.ProcessId})";
        var text = Diagnostics.Build(stateLine, config, branch, security, extra,
            settings.AutoRestartServer, watchdogRestartCount,
            lastWatchdogRestartAt?.ToString("HH:mm"), lastMaintenance, logLines);
        try { Clipboard.SetText(text); }
        catch (ExternalException) { ShowToast("复制失败，剪贴板被其他程序占用。", ToastKind.Warning); return; }
        ShowToast("已复制诊断信息", ToastKind.Success);
        await FlashCheck(copyDiagButton);
    }

    private static async Task FlashCheck(ThemedIconButton button)
    {
        var original = button.Glyph;
        button.Glyph = Icons.Check;
        button.Invalidate();
        await Task.Delay(1500);
        if (button.IsDisposed || button.Disposing) return;
        button.Glyph = original;
        button.Invalidate();
    }

    /// <summary>
    /// Maps the selected text back to the full source lines so copied text keeps
    /// complete timestamps even though the view collapses them. The view holds one
    /// hard line per rendered entry, so text lines align with renderedEntries by index.
    /// </summary>
    private string SelectedLogText()
    {
        var text = logView.Text;
        if (renderedEntries.Count == 0 || text.Length == 0) return logView.SelectedText;
        var selStart = Math.Clamp(logView.SelectionStart, 0, text.Length);
        var selEnd = Math.Clamp(logView.SelectionStart + logView.SelectionLength, 0, text.Length);
        var lines = text.Split('\n');
        var result = new List<string>();
        var offset = 0;
        for (var i = 0; i < lines.Length && offset <= selEnd; i++)
        {
            var lineEnd = offset + lines[i].Length;
            if (lineEnd >= selStart && i < renderedEntries.Count) result.Add(renderedEntries[i].Source);
            offset = lineEnd + 1;
        }
        return string.Join("\n", result);
    }

    private void OpenServer() { if (server.Config is { } config) OpenPath(config.LocalUrl); else ShowToast("尚未读取服务地址。", ToastKind.Info); }

    private void OpenReadme()
    {
        var directory = AppContext.BaseDirectory;
        for (var i = 0; i < 5 && directory is not null; i++)
        {
            var candidate = Path.Combine(directory, "README.md");
            if (File.Exists(candidate)) { OpenPath(candidate); return; }
            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }
        OpenPath(ManagerDistribution.ProjectUrl + "#readme");
    }
}
