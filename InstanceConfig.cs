using System.Text.Json;

namespace SillyTavernTray;

internal sealed record InstanceConfig(int Port, bool Ssl, bool Ipv4, bool Ipv6, bool Whitelist,
    bool BasicAuth, bool UserAccounts, string DataRoot, string Version)
{
    public string LocalUrl => $"{(Ssl ? "https" : "http")}://{(Ipv4 ? "127.0.0.1" : "[::1]")}:{Port}/";

    public static async Task<InstanceConfig> LoadAsync(string directory, string node)
    {
        const string script = """
            const fs = require('fs');
            let yaml;
            try { yaml = require('yaml'); } catch { yaml = null; }
            // Dependency repair must remain available when node_modules/yaml is missing.
            // Only accept simple scalars for the fields used here; fail closed for complex YAML.
            const parseSimple = text => {
                const result = {}, top = new Set(['port','whitelistMode','basicAuthMode','enableUserAccounts','dataRoot']);
                const nested = { ssl: new Set(['enabled']), protocol: new Set(['ipv4','ipv6']) };
                let section = '';
                const scalar = raw => {
                    const v = raw.replace(/\s+#.*$/, '').trim();
                    if (/^'(?:[^']|'')*'$/.test(v)) return v.slice(1,-1).replace(/''/g,"'");
                    if (/^"(?:[^"\\]|\\.)*"$/.test(v)) return JSON.parse(v);
                    if (/^\d+$/.test(v)) return Number(v);
                    if (/^(true|false|yes|no|auto)$/i.test(v)) return v.toLowerCase();
                    if (/^[\w.\/\\:-]+$/.test(v)) return v;
                    throw new Error('Config contains a complex value; install dependencies or inspect config.yaml.');
                };
                for (const line of text.replace(/^\uFEFF/,'').split(/\r?\n/)) {
                    const match = line.match(/^([A-Za-z][\w]*):(?:\s*(.*))?$/);
                    if (match) {
                        const key = match[1], raw = (match[2] || '').replace(/\s+#.*$/,'').trim();
                        section = key in nested ? key : '';
                        if (section) {
                            if (raw) throw new Error('Complex nested config requires the yaml dependency.');
                            result[key] = {};
                        } else if (top.has(key)) result[key] = scalar(raw);
                        continue;
                    }
                    const item = line.match(/^\s+([A-Za-z][\w]*):\s*(.*)$/);
                    if (section && item && nested[section].has(item[1])) result[section][item[1]] = scalar(item[2]);
                }
                return result;
            };
            const read = p => {
                if (!fs.existsSync(p)) return {};
                const text = fs.readFileSync(p,'utf8');
                return (yaml ? yaml.parse(text) : parseSimple(text)) || {};
            };
            const defaults = read('default/config.yaml'), user = read('config.yaml');
            const get = (a,b) => a === undefined || a === null ? b : a;
            const val = (key, fallback) => get(user[key], get(defaults[key], fallback));
            const sub = (key, field, fallback) => get(user[key]?.[field], get(defaults[key]?.[field], fallback));
            const env = (key, fallback) => process.env[key] === undefined ? fallback : process.env[key];
            const bool = v => v === true || v === 'true' || v === 'yes' || v === 'auto';
            const port = Number(env('SILLYTAVERN_PORT', val('port', 8000)));
            const info = { port, ssl: bool(env('SILLYTAVERN_SSL_ENABLED', sub('ssl','enabled',false))),
                ipv4: bool(env('SILLYTAVERN_PROTOCOL_IPV4', sub('protocol','ipv4',true))),
                ipv6: bool(env('SILLYTAVERN_PROTOCOL_IPV6', sub('protocol','ipv6',false))),
                whitelist: bool(env('SILLYTAVERN_WHITELISTMODE', val('whitelistMode',true))),
                basicAuth: bool(env('SILLYTAVERN_BASICAUTHMODE', val('basicAuthMode',false))),
                userAccounts: bool(env('SILLYTAVERN_ENABLEUSERACCOUNTS', val('enableUserAccounts',false))),
                dataRoot: String(env('SILLYTAVERN_DATAROOT', val('dataRoot','./data'))) };
            process.stdout.write(JSON.stringify(info));
            """;
        var result = await CommandRunner.RunAsync(node, ["-e", script], directory);
        if (result.ExitCode != 0) throw new InvalidOperationException("无法读取 SillyTavern 配置，请检查 Node.js 和项目依赖。\n" + result.Error.Trim());
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        var port = root.GetProperty("port").GetInt32();
        if (port is < 1 or > 65535) throw new InvalidOperationException("SillyTavern 配置的端口无效。");
        var data = root.GetProperty("dataRoot").GetString() ?? "data";
        var version = "未知";
        var packagePath = Path.Combine(directory, "package.json");
        if (File.Exists(packagePath))
        {
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(packagePath));
            if (package.RootElement.TryGetProperty("version", out var v)) version = v.GetString() ?? "未知";
        }
        return new InstanceConfig(port, root.GetProperty("ssl").GetBoolean(), root.GetProperty("ipv4").GetBoolean(),
            root.GetProperty("ipv6").GetBoolean(), root.GetProperty("whitelist").GetBoolean(),
            root.GetProperty("basicAuth").GetBoolean(), root.GetProperty("userAccounts").GetBoolean(),
            Path.GetFullPath(data, directory), version);
    }
}
