using System.Net;
using Microsoft.Win32;

namespace SillyTavernTray;

internal sealed record ProxyRoute(Uri Destination, Uri? Proxy)
{
    public string Description => Proxy is null ? "直连" : "使用系统代理";
}

/// <summary>Resolves the current Windows user proxy for each maintenance operation.</summary>
internal sealed class SystemProxyResolver
{
    private readonly Func<IWebProxy> getProxy;
    private readonly Func<string?> getBypassList;

    public SystemProxyResolver(Func<IWebProxy>? getProxy = null, Func<string?>? getBypassList = null)
    {
#pragma warning disable SYSLIB0014 // GetSystemWebProxy is the API that reads the Windows user's Internet Options, including PAC.
        this.getProxy = getProxy ?? WebRequest.GetSystemWebProxy;
#pragma warning restore SYSLIB0014
        this.getBypassList = getBypassList ?? ReadWindowsBypassList;
    }

    public async Task<ProxyRoute> ResolveAsync(Uri destination)
    {
        if (destination.Scheme is not ("http" or "https")) throw new ArgumentException("仅支持 HTTP(S) 地址。", nameof(destination));
        var safeDestination = WithoutCredentials(destination);
        try
        {
            return await Task.Run(() => Resolve(getProxy(), safeDestination)).WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException ex) { throw new InvalidOperationException("读取 Windows 系统代理超时，请检查系统代理或自动配置脚本。", ex); }
        catch (Exception ex) when (ex is not InvalidOperationException)
        { throw new InvalidOperationException("读取 Windows 系统代理失败：" + ex.Message, ex); }
    }

    private static ProxyRoute Resolve(IWebProxy settings, Uri destination)
    {
        if (settings.IsBypassed(destination)) return new ProxyRoute(destination, null);
        var proxy = settings.GetProxy(destination);
        if (proxy is null || proxy == destination) return new ProxyRoute(destination, null);
        if (proxy.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5" or "socks5h"))
            throw new InvalidOperationException("Windows 返回了不支持的代理协议：" + proxy.Scheme);
        return new ProxyRoute(destination, proxy);
    }

    public static Uri WithoutCredentials(Uri uri)
    {
        var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
        return builder.Uri;
    }

    /// <summary>Git's command-scope configuration wins over existing global and repository proxy settings.</summary>
    public static IReadOnlyDictionary<string, string> ForGit(ProxyRoute route)
    {
        var env = EmptyProxyEnvironment();
        var value = route.Proxy?.AbsoluteUri ?? ""; // Git treats an empty http.proxy as direct.
        var currentCount = Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT");
        if (!string.IsNullOrEmpty(currentCount) && (!int.TryParse(currentCount, out var parsed) || parsed < 0 || parsed > 1024))
            throw new InvalidOperationException("现有 Git 临时配置无效，无法设置系统代理。");
        var count = string.IsNullOrEmpty(currentCount) ? 0 : int.Parse(currentCount);
        env[$"GIT_CONFIG_KEY_{count}"] = "http.proxy";
        env[$"GIT_CONFIG_VALUE_{count++}"] = value;
        // A URL-specific proxy can otherwise override http.proxy even at a lower configuration scope.
        env[$"GIT_CONFIG_KEY_{count}"] = "http." + route.Destination.AbsoluteUri.TrimEnd('/') + ".proxy";
        env[$"GIT_CONFIG_VALUE_{count++}"] = value;
        env["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }

    public IReadOnlyDictionary<string, string> ForNpm(ProxyRoute http, ProxyRoute https, Uri registry)
    {
        if (new[] { http.Proxy, https.Proxy }.Any(p => p is not null && p.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("当前系统代理使用 SOCKS，npm 无法直接使用该代理；请在 Windows 中启用 HTTP 代理。");
        var env = EmptyProxyEnvironment();
        env["HTTP_PROXY"] = http.Proxy?.AbsoluteUri ?? "";
        env["HTTPS_PROXY"] = https.Proxy?.AbsoluteUri ?? "";
        // npm ignores empty config environment variables; "false" explicitly disables a proxy from .npmrc.
        env["npm_config_proxy"] = http.Proxy?.AbsoluteUri ?? "false";
        env["npm_config_https_proxy"] = https.Proxy?.AbsoluteUri ?? "false";
        var registryRoute = registry.Scheme == "https" ? https : http;
        var bypass = TranslateBypassList(getBypassList(), registry.Host, registryRoute.Proxy is not null);
        // A nonempty, impossible hostname overrides noproxy entries inherited from .npmrc without bypassing real hosts.
        env["NO_PROXY"] = bypass;
        env["npm_config_noproxy"] = bypass;
        return env;
    }

    private static Dictionary<string, string> EmptyProxyEnvironment() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["HTTP_PROXY"] = "", ["HTTPS_PROXY"] = "", ["ALL_PROXY"] = "", ["NO_PROXY"] = ""
    };

    private static string? ReadWindowsBypassList()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return key?.GetValue("ProxyOverride") as string;
    }

    internal static string TranslateBypassList(string? windowsList, string registryHost, bool registryUsesProxy)
    {
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (windowsList ?? "").Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("<local>", StringComparison.OrdinalIgnoreCase)) { entries.Add("localhost"); continue; }
            var token = raw.StartsWith("*.", StringComparison.Ordinal) ? raw[1..] : raw;
            if (token != "*" && (token.Contains('*') || token.Contains('/') || token.Contains(':'))) continue;
            if (token.Length > 0) entries.Add(token);
        }
        if (registryUsesProxy)
        {
            entries.RemoveWhere(entry => entry == "*" || entry.Equals(registryHost, StringComparison.OrdinalIgnoreCase) ||
                (entry.StartsWith('.') && (registryHost.Equals(entry[1..], StringComparison.OrdinalIgnoreCase) || registryHost.EndsWith(entry, StringComparison.OrdinalIgnoreCase))));
        }
        return entries.Count == 0 ? "-" : string.Join(',', entries);
    }
}
