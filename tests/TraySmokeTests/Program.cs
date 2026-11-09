using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using SillyTavernTray;
using SillyTavernTray.Ui;
using Microsoft.Win32;

internal static class TestProgram
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--signal-console" && uint.TryParse(args[1], out var pid))
            return ConsoleSignal.SendInterrupt(pid) ? 0 : 1;
        if (args.Length == 2 && args[0] == "--probe-system") return await ProbeSystemProxyAsync(args[1]);

        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var temp = Path.Combine(tempRoot, "SillyTavernTray-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldData = Environment.GetEnvironmentVariable("ST_TRAY_DATA_DIR");
        var oldMarker = Environment.GetEnvironmentVariable("ST_TRAY_TEST_MARKER");
        var oldArgs = Environment.GetEnvironmentVariable("ST_TRAY_TEST_ARGS");
        ServerController? server = null;
        try
        {
            Environment.SetEnvironmentVariable("ST_TRAY_DATA_DIR", Path.Combine(temp, "manager data"));
            Environment.SetEnvironmentVariable("ST_TRAY_TEST_MARKER", Path.Combine(temp, "stopped.txt"));
            Environment.SetEnvironmentVariable("ST_TRAY_TEST_ARGS", Path.Combine(temp, "args.txt"));
            var realNpm = CommandRunner.FindExecutable("npm.cmd") ?? throw new Exception("npm missing");
            var npmFixture = Path.Combine(temp, "真实 npm 目录");
            Directory.CreateDirectory(npmFixture);
            File.WriteAllText(Path.Combine(npmFixture, "package.json"), "{\"name\":\"tray-offline-fixture\",\"version\":\"1.0.0\",\"private\":true}\n");
            File.WriteAllText(Path.Combine(npmFixture, ".npmrc"), "registry=http://127.0.0.1:1/\nupdate-notifier=false\n");
            var realNpmResult = await CommandRunner.RunNpmInstallAsync(realNpm, npmFixture, _ => { });
            Assert(realNpmResult.ExitCode == 0, $"real offline npm install (exit {realNpmResult.ExitCode}; {realNpmResult.Error})");
            Console.WriteLine("PASS: real npm install command on an offline empty fixture");
            var toolDir = Path.Combine(temp, "工具 path");
            Directory.CreateDirectory(toolDir);
            var npm = Path.Combine(toolDir, "npm.cmd");
            File.WriteAllText(npm, "@echo off\r\nif /i \"%~1\"==\"config\" (echo https://registry.npmjs.org/ & exit /b 0)\r\necho npm-stub-ok\r\nexit /b 0\r\n");
            Environment.SetEnvironmentVariable("PATH", toolDir + ";" + oldPath);
            var git = CommandRunner.FindExecutable("git.exe") ?? throw new Exception("Git missing");
            var node = CommandRunner.FindExecutable("node.exe") ?? throw new Exception("Node missing");
            var work = Path.Combine(temp, "fixture 目录");
            var origin = Path.Combine(temp, "remote.git");
            var writer = Path.Combine(temp, "writer 目录");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(Path.Combine(work, "node_modules", "yaml"));
            Directory.CreateDirectory(Path.Combine(work, "default"));
            var port = FreePort();
            WriteConfig(work, port);
            File.WriteAllText(Path.Combine(work, "default", "config.yaml"), "{}");
            File.WriteAllText(Path.Combine(work, "node_modules", "yaml", "index.js"), "exports.parse = text => JSON.parse(text);\n");
            File.WriteAllText(Path.Combine(work, "package.json"), "{\"name\":\"test-fixture\",\"version\":\"1.0.0\"}\n");
            File.WriteAllText(Path.Combine(work, "server.js"), """
                const http = require('http');
                const fs = require('fs');
                const c = JSON.parse(fs.readFileSync('config.yaml','utf8'));
                fs.writeFileSync(process.env.ST_TRAY_TEST_ARGS, JSON.stringify(process.argv));
                const server = http.createServer((req,res) => { res.writeHead(200); res.end('fixture'); });
                process.on('SIGINT', () => {
                    fs.writeFileSync(process.env.ST_TRAY_TEST_MARKER, 'graceful');
                    console.log('normal shutdown');
                    server.close(() => process.exit(0));
                });
                server.listen(c.port, '127.0.0.1', () => console.log('SillyTavern is listening on 127.0.0.1:' + c.port));
                """);
            await Run(git, temp, "init", "--bare", origin);
            await Run(git, work, "init", "-b", "release");
            await Run(git, work, "config", "user.email", "test@example.invalid");
            await Run(git, work, "config", "user.name", "Test");
            await Run(git, work, "remote", "add", "origin", origin);
            await Run(git, work, "add", "-f", ".");
            await Run(git, work, "commit", "-m", "initial");
            await Run(git, work, "push", "-u", "origin", "release");
            await Run(git, temp, "clone", "-b", "release", origin, writer);
            await Run(git, writer, "config", "user.email", "test@example.invalid");
            await Run(git, writer, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(writer, "package.json"), "{\"name\":\"test-fixture\",\"version\":\"1.0.1\"}\n");
            await Run(git, writer, "add", "package.json");
            await Run(git, writer, "commit", "-m", "fixture update");
            await Run(git, writer, "push", "origin", "release");

            var npmResult = await CommandRunner.RunNpmInstallAsync(npm, work, _ => { });
            Assert(npmResult.ExitCode == 0 && npmResult.Output.Contains("npm-stub-ok"), $"npm.cmd invocation with Unicode and spaces (exit {npmResult.ExitCode}; out: {npmResult.Output}; err: {npmResult.Error})");
            Console.WriteLine("PASS: npm command path with Unicode and spaces");

            var registryKey = @"Software\SillyTavernTrayVerify_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var setupKey = Registry.CurrentUser.CreateSubKey(registryKey)) setupKey.SetValue("unrelated", "keep");
                AutoStart.SetEnabled(true, registryKey);
                Assert(AutoStart.IsEnabled(registryKey), "autostart registration");
                AutoStart.SetEnabled(false, registryKey);
                using var registry = Registry.CurrentUser.OpenSubKey(registryKey);
                Assert(!AutoStart.IsEnabled(registryKey) && (string?)registry?.GetValue("unrelated") == "keep", "autostart removal leaves unrelated values");
            }
            finally { Registry.CurrentUser.DeleteSubKey(registryKey, throwOnMissingSubKey: false); }
            Console.WriteLine("PASS: autostart registration and removal in an isolated registry key");

            await VerifyProxyRoutingAsync(git, node, realNpm, work);
            VerifyReleaseParsing();
            VerifyAppStateMapping();
            Console.WriteLine("PASS: manager release metadata and download origin validation");

            var settings = new AppSettings { InstallDirectory = work };
            var log = new AppLog();
            server = new ServerController(settings, log);
            var maintenance = new MaintenanceService(settings, log, server, new SystemProxyResolver(() => new WebProxy(), () => null));
            await server.StartAsync(isAutostart: true);
            await WaitUntil(() => server.State == ServerState.Running, "server ready");
            var argText = File.ReadAllText(Path.Combine(temp, "args.txt"));
            Assert(argText.Contains("--listen") && argText.Contains("--browserLaunchEnabled") && argText.Contains("false"), "autostart arguments");
            Console.WriteLine("PASS: hidden server starts with listen and silent autostart arguments");

            var dirtyFile = Path.Combine(work, "untracked.txt");
            File.WriteAllText(dirtyFile, "do not overwrite");
            try { await maintenance.UpdateAsync(() => Task.FromResult(StopDecision.Cancel)); throw new Exception("dirty repository accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("本地修改")) { }
            Assert(server.HasOwnedProcess && !File.Exists(Path.Combine(temp, "stopped.txt")), "preflight must leave running service untouched");
            File.Delete(dirtyFile);
            Console.WriteLine("PASS: dirty update preflight does not stop server");

            var check = await maintenance.CheckUpdatesAsync();
            Assert(check.Contains("发现更新"), "update availability detection");
            await maintenance.UpdateAsync(() => Task.FromResult(StopDecision.Cancel));
            await WaitUntil(() => server.State == ServerState.Running && server.Config?.Version == "1.0.1", "updated server ready");
            Assert(File.ReadAllText(Path.Combine(temp, "stopped.txt")) == "graceful", "SIGINT graceful shutdown handler");
            Console.WriteLine("PASS: fetch, official pull, dependency command, graceful stop and restart");

            File.WriteAllText(npm, "@echo off\r\nif /i \"%~1\"==\"config\" (echo https://registry.npmjs.org/ & exit /b 0)\r\necho simulated-failure 1>&2\r\nexit /b 7\r\n");
            try { await maintenance.UpdateAsync(() => Task.FromResult(StopDecision.Cancel)); throw new Exception("failed npm accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("依赖安装失败")) { }
            await WaitUntil(() => !server.HasOwnedProcess, "failed update leaves stopped");
            Console.WriteLine("PASS: failed dependency install leaves server stopped");

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var occupied = ((IPEndPoint)listener.LocalEndpoint).Port;
            WriteConfig(work, occupied);
            await server.StartAsync();
            Assert(server.State == ServerState.Occupied && !server.HasOwnedProcess, "occupied port protection");
            using var connection = new TcpClient();
            await connection.ConnectAsync(IPAddress.Loopback, occupied);
            Console.WriteLine("PASS: occupied port is reported and external listener remains alive");

            Assert(log.ReadRecent().Any(x => x.Contains("normal shutdown")), "service output captured to history");
            Console.WriteLine("PASS: service output persisted in history");
            File.Delete(Path.Combine(work, "node_modules", "yaml", "index.js"));
            File.WriteAllText(Path.Combine(work, "config.yaml"), $"port: {occupied}\nssl:\n  enabled: false\nprotocol:\n  ipv4: true\n  ipv6: false\nwhitelistMode: true\ndataRoot: ./data\n");
            var fallback = await InstanceConfig.LoadAsync(work, node);
            Assert(fallback.Port == occupied && fallback.Ipv4 && fallback.Whitelist, "configuration fallback without YAML dependency");
            Console.WriteLine("PASS: configuration remains readable when YAML dependency is missing");
            var updateFixture = Environment.GetEnvironmentVariable("ST_TRAY_TEST_MANAGER_UPDATE_EXE");
            var oldFixture = Environment.GetEnvironmentVariable("ST_TRAY_TEST_MANAGER_CURRENT_EXE");
            if (!string.IsNullOrEmpty(updateFixture) && !string.IsNullOrEmpty(oldFixture))
                await VerifyManagerUpdaterAsync(temp, work, node, updateFixture, oldFixture, log);
            if (!string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            {
                new AppSettings { InstallDirectory = work, StartServerOnLaunch = false, AutoUpdateManager = false }.Save();
                VerifyTrayOnlyWindow();
                Console.WriteLine("PASS: management window stays out of the taskbar before and after restore");
            }
            else Console.WriteLine("SKIP: interactive tray window check on CI");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            foreach (var line in new AppLog().ReadRecent().TakeLast(30)) Console.Error.WriteLine(line);
            return 1;
        }
        finally
        {
            if (server is { HasOwnedProcess: true }) await server.StopAsync(() => Task.FromResult(StopDecision.Force));
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("ST_TRAY_DATA_DIR", oldData);
            Environment.SetEnvironmentVariable("ST_TRAY_TEST_MARKER", oldMarker);
            Environment.SetEnvironmentVariable("ST_TRAY_TEST_ARGS", oldArgs);
            var resolved = Path.GetFullPath(temp).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(Path.GetDirectoryName(resolved), tempRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("SillyTavernTray-verify-", StringComparison.Ordinal))
            {
                try { Directory.Delete(resolved, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static void WriteConfig(string directory, int port) => File.WriteAllText(Path.Combine(directory, "config.yaml"),
        $"{{\"port\":{port},\"ssl\":{{\"enabled\":false}},\"protocol\":{{\"ipv4\":true,\"ipv6\":false}},\"whitelistMode\":true,\"dataRoot\":\"data\"}}\n");
    private static async Task Run(string executable, string dir, params string[] args)
    {
        var result = await CommandRunner.RunAsync(executable, args, dir);
        if (result.ExitCode != 0) throw new Exception($"Command failed: {Path.GetFileName(executable)} {string.Join(' ', args)}\n{result.Error}\n{result.Output}");
    }
    private static void Assert(bool condition, string description) { if (!condition) throw new Exception("Assertion failed: " + description); }
    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(100); }
        throw new Exception("Timed out: " + description);
    }

    private static async Task VerifyProxyRoutingAsync(string git, string node, string npm, string work)
    {
        var testTarget = new Uri("https://name:secret@fixture.invalid/repository.git");
        using var gitListener = new TcpListener(IPAddress.Loopback, 0);
        gitListener.Start();
        var gitPort = ((IPEndPoint)gitListener.LocalEndpoint).Port;
        var fakeSystem = new SystemProxyResolver(() => new WebProxy($"http://127.0.0.1:{gitPort}"), () => null);
        var route = await fakeSystem.ResolveAsync(testTarget);
        Assert(route.Proxy?.Port == gitPort && string.IsNullOrEmpty(route.Destination.UserInfo), "system proxy and credential stripping");
        await Run(git, work, "config", "http.proxy", "http://127.0.0.1:1");
        await Run(git, work, "config", "http.https://fixture.invalid/repository.git.proxy", "http://127.0.0.1:2");
        var gitEnvironment = SystemProxyResolver.ForGit(route);
        var gitTask = CommandRunner.RunAsync(git, ["ls-remote", "https://fixture.invalid/repository.git"], work, environment: gitEnvironment);
        using (var connection = await gitListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12)))
        {
            var request = await ReadRequestAsync(connection);
            Assert(request.Contains("CONNECT fixture.invalid:443", StringComparison.OrdinalIgnoreCase), "git used system proxy");
            await ReplyAsync(connection, "502 Bad Gateway", "");
        }
        await gitTask.WaitAsync(TimeSpan.FromSeconds(12));
        Console.WriteLine("PASS: Git HTTP(S) traffic uses system proxy despite existing repository proxy settings");

        using var npmListener = new TcpListener(IPAddress.Loopback, 0);
        npmListener.Start();
        var npmPort = ((IPEndPoint)npmListener.LocalEndpoint).Port;
        var npmResolver = new SystemProxyResolver(() => new WebProxy($"http://127.0.0.1:{npmPort}"), () => "*.private.invalid;localhost");
        var registry = new Uri("http://registry.fixture.invalid/");
        var httpRoute = await npmResolver.ResolveAsync(registry);
        var httpsRoute = await npmResolver.ResolveAsync(new Uri("https://registry.fixture.invalid/"));
        var npmEnvironment = new Dictionary<string, string>(npmResolver.ForNpm(httpRoute, httpsRoute, registry), StringComparer.OrdinalIgnoreCase)
        {
            ["npm_config_fetch_retries"] = "0", ["npm_config_fetch_timeout"] = "5000"
        };
        File.WriteAllText(Path.Combine(work, ".npmrc"), "proxy=http://127.0.0.1:1\nhttps-proxy=http://127.0.0.1:2\nnoproxy=registry.fixture.invalid\n");
        var npmCli = Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm", "bin", "npm-cli.js");
        var npmTask = CommandRunner.RunAsync(node, [npmCli, "ping", "--registry=http://registry.fixture.invalid/"], work, environment: npmEnvironment);
        using (var connection = await npmListener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(12)))
        {
            var request = await ReadRequestAsync(connection);
            Assert(request.Contains("registry.fixture.invalid", StringComparison.OrdinalIgnoreCase), "npm used system proxy");
            await ReplyAsync(connection, "200 OK", "{}");
        }
        var npmResult = await npmTask.WaitAsync(TimeSpan.FromSeconds(12));
        Assert(npmResult.ExitCode == 0, "npm proxy request completed");
        Console.WriteLine("PASS: real npm HTTP traffic uses system proxy");

        var directResolver = new SystemProxyResolver(() => new WebProxy(), () => null);
        var directHttp = await directResolver.ResolveAsync(new Uri("http://registry.fixture.invalid/"));
        var directHttps = await directResolver.ResolveAsync(new Uri("https://registry.fixture.invalid/"));
        var directEnvironment = directResolver.ForNpm(directHttp, directHttps, registry);
        File.WriteAllText(Path.Combine(work, ".npmrc"), "proxy=http://127.0.0.1:1\nhttps-proxy=http://127.0.0.1:2\n");
        try
        {
            var directConfig = await CommandRunner.RunAsync(node, [npmCli, "config", "get", "https-proxy"], work, environment: directEnvironment);
            Assert(directConfig.ExitCode == 0 && directConfig.Output.Trim() == "false", "system direct overrides npmrc proxy");
        }
        finally { File.Delete(Path.Combine(work, ".npmrc")); }
        Assert(SystemProxyResolver.TranslateBypassList("*.private.invalid;registry.fixture.invalid", registry.Host, true) == ".private.invalid", "bypass translation preserves selected proxy");
        Console.WriteLine("PASS: system direct and bypass rules override conflicting npm configuration");
    }

    private static async Task<string> ReadRequestAsync(TcpClient connection)
    {
        var stream = connection.GetStream();
        var bytes = new byte[8192];
        var text = new StringBuilder();
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && text.Length < 32768)
        {
            var count = await stream.ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(8));
            if (count == 0) break;
            text.Append(Encoding.ASCII.GetString(bytes, 0, count));
        }
        return text.ToString();
    }

    private static async Task ReplyAsync(TcpClient connection, string status, string body)
    {
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await connection.GetStream().WriteAsync(bytes);
    }

    private static async Task<int> ProbeSystemProxyAsync(string directory)
    {
        try
        {
            var git = CommandRunner.FindExecutable("git.exe") ?? throw new Exception("Git missing");
            var npm = CommandRunner.FindExecutable("npm.cmd") ?? throw new Exception("npm missing");
            var node = CommandRunner.FindExecutable("node.exe") ?? throw new Exception("Node missing");
            var upstream = await CommandRunner.RunAsync(git, ["remote", "get-url", "origin"], directory);
            if (upstream.ExitCode != 0 || !Uri.TryCreate(upstream.Output.Trim(), UriKind.Absolute, out var gitUrl) || gitUrl.Scheme is not ("http" or "https"))
                throw new Exception("No HTTP(S) Git origin");
            var resolver = new SystemProxyResolver();
            var gitRoute = await resolver.ResolveAsync(gitUrl);
            var gitResult = await CommandRunner.RunAsync(git, ["config", "--get-urlmatch", "http.proxy", gitRoute.Destination.AbsoluteUri], directory,
                environment: SystemProxyResolver.ForGit(gitRoute));
            Assert(gitResult.ExitCode == 0 && SameProxy(gitResult.Output.Trim(), gitRoute.Proxy), "actual Git proxy configuration");

            var npmRegistry = await CommandRunner.GetNpmRegistryAsync(npm, directory);
            if (npmRegistry.ExitCode != 0 || !Uri.TryCreate(npmRegistry.Output.Trim(), UriKind.Absolute, out var registry)) throw new Exception("Invalid npm registry");
            var http = await resolver.ResolveAsync(new UriBuilder(registry) { Scheme = "http", Port = registry.IsDefaultPort ? -1 : registry.Port }.Uri);
            var https = await resolver.ResolveAsync(new UriBuilder(registry) { Scheme = "https", Port = registry.IsDefaultPort ? -1 : registry.Port }.Uri);
            var npmCli = Path.Combine(Path.GetDirectoryName(npm)!, "node_modules", "npm", "bin", "npm-cli.js");
            var configName = registry.Scheme == "https" ? "https-proxy" : "proxy";
            var npmResult = await CommandRunner.RunAsync(node, [npmCli, "config", "get", configName], directory,
                environment: resolver.ForNpm(http, https, registry));
            Assert(npmResult.ExitCode == 0 && SameProxy(npmResult.Output.Trim(), registry.Scheme == "https" ? https.Proxy : http.Proxy), "actual npm proxy configuration");
            Console.WriteLine("PASS: Git and npm receive the current Windows system proxy (configuration read only).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: system-proxy probe: " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static bool SameProxy(string configured, Uri? expected)
    {
        if (expected is null) return configured is "" or "false" or "null";
        return Uri.TryCreate(configured, UriKind.Absolute, out var actual) &&
            actual.Scheme == expected.Scheme && actual.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase) && actual.Port == expected.Port;
    }

    private static void VerifyAppStateMapping()
    {
        var running = AppStateFactory.Compute(ServerState.Running, true, BusyOperation.None, ConfigurationState.Configured, "运行中", "meta");
        Assert(running.Tone == StatusTone.Running && running.PrimaryKind == PrimaryActionKind.OpenServer && running.AddressEnabled, "running primary state");
        Assert(running.Actions.Any(a => a.Kind == ViewActionKind.Stop), "running stop action");

        var stopped = AppStateFactory.Compute(ServerState.Stopped, false, BusyOperation.None, ConfigurationState.Configured, "未运行", "meta");
        Assert(stopped.Tone == StatusTone.Stopped && stopped.PrimaryKind == PrimaryActionKind.Start, "stopped primary state");
        Assert(stopped.Actions.Count == 1 && !stopped.Actions[0].Enabled, "stopped open action disabled");

        var busy = AppStateFactory.Compute(ServerState.Starting, false, BusyOperation.Starting, ConfigurationState.Configured, "正在启动", "meta");
        Assert(busy.Tone == StatusTone.Busy && busy.ShowProgress && busy.PrimaryKind == PrimaryActionKind.None, "busy progress state");

        var occupied = AppStateFactory.Compute(ServerState.Occupied, false, BusyOperation.None, ConfigurationState.Configured, "端口 8000 已被占用", "meta");
        Assert(occupied.Tone == StatusTone.Error && occupied.PrimaryKind == PrimaryActionKind.Retry, "occupied retry state");

        var unconfigured = AppStateFactory.Compute(ServerState.Stopped, false, BusyOperation.None, ConfigurationState.Unconfigured, "未运行", "meta");
        Assert(unconfigured.OnboardingVisible && unconfigured.PrimaryKind == PrimaryActionKind.ChooseDirectory, "unconfigured onboarding state");

        var configError = AppStateFactory.Compute(ServerState.Stopped, false, BusyOperation.None, ConfigurationState.Error, "读取失败", "meta");
        Assert(configError.Tone == StatusTone.Error && configError.PrimaryKind == PrimaryActionKind.Retry, "configuration error state");

        Console.WriteLine("PASS: view state mapping covers running, stopped, busy, occupied, unconfigured and error");
    }

    private static void VerifyReleaseParsing()
    {
        var hash = new string('a', 64);
        var valid = ReleaseJson("v1.2.3", 123, hash);
        using var document = JsonDocument.Parse(valid);
        var release = ManagerUpdateService.ParseRelease(document.RootElement);
        Assert(release.Version == new Version(1, 2, 3) && release.Sha256 == hash, "valid manager release");
        foreach (var invalid in new[]
        {
            valid.Replace("https://github.com/", "https://attacker.invalid/", StringComparison.Ordinal),
            valid.Replace("sha256:" + hash, "sha256:invalid", StringComparison.Ordinal),
            valid.Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal),
            ReleaseJson("v1.2.3-beta", 123, hash)
        })
        {
            using var bad = JsonDocument.Parse(invalid);
            try { ManagerUpdateService.ParseRelease(bad.RootElement); throw new Exception("invalid release accepted"); }
            catch (InvalidDataException) { }
        }
    }

    private static string ReleaseJson(string tag, long size, string hash) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft = false, prerelease = false,
        assets = new[] { new { name = ManagerUpdateService.AssetName, size, digest = "sha256:" + hash,
            browser_download_url = $"https://github.com/wacilimonster-source/SillyTavernDesktop/releases/download/{tag}/{ManagerUpdateService.AssetName}" } }
    });

    private static async Task VerifyManagerUpdaterAsync(string temp, string work, string node, string newExe, string oldExe, AppLog log)
    {
        var targetDir = Path.Combine(temp, "manager update target");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "SillyTavernTray.exe");
        File.Copy(oldExe, target);
        var oldHash = PendingManagerUpdateStore.HashFile(target);
        var newHash = PendingManagerUpdateStore.HashFile(newExe);
        var corrupt = Path.Combine(temp, "corrupt-manager.exe");
        File.WriteAllBytes(corrupt, [0x4d, 0x5a, 0x00, 0x01]);
        using (var badClient = new HttpClient(new FakeReleaseHandler(ReleaseJson("v1.0.1", 4, new string('0', 64)), corrupt)))
        using (var badService = new ManagerUpdateService(log, badClient, target, new Version(1, 0, 0)))
        {
            try { await badService.CheckAsync(manual: false); throw new Exception("corrupt manager update accepted"); }
            catch (InvalidDataException) { }
            Assert(PendingManagerUpdateStore.Read() is null && PendingManagerUpdateStore.HashFile(target) == oldHash, "corrupt manager update rejected");
        }
        Console.WriteLine("PASS: corrupted manager download is rejected without changing the executable");
        var metadata = ReleaseJson("v1.0.1", new FileInfo(newExe).Length, newHash);
        File.Copy(newExe, target, overwrite: true);
        using (var newerClient = new HttpClient(new FakeReleaseHandler(metadata, newExe)))
        using (var newerService = new ManagerUpdateService(log, newerClient, target, new Version(1, 0, 0)))
        {
            try { await newerService.CheckAsync(manual: false); throw new Exception("same on-disk version accepted"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("已经是该版本")) { }
            Assert(PendingManagerUpdateStore.Read() is null && PendingManagerUpdateStore.HashFile(target) == newHash, "newer on-disk manager preserved");
        }
        File.Copy(oldExe, target, overwrite: true);
        Console.WriteLine("PASS: an already updated on-disk manager is not overwritten");
        using var client = new HttpClient(new FakeReleaseHandler(metadata, newExe));
        using var service = new ManagerUpdateService(log, client, target, new Version(1, 0, 0));
        var status = await service.CheckAsync(manual: false);
        var pending = PendingManagerUpdateStore.Read() ?? throw new Exception("pending manager update missing");
        var stage = PendingManagerUpdateStore.StagePath(pending);
        Assert(status.Contains("已下载") && File.Exists(stage) && PendingManagerUpdateStore.HashFile(stage) == newHash, "manager download and checksum");
        Assert(PendingManagerUpdateStore.HashFile(target) == oldHash, "manager is not replaced during download");
        Console.WriteLine("PASS: manager release downloads and verifies without replacing the running version");

        using var parent = Process.Start(new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            ArgumentList = { "-e", "process.stdin.resume();process.stdin.on('data',()=>process.exit(0));" }
        }) ?? throw new Exception("fixture parent not started");
        var parentStart = parent.StartTime.ToUniversalTime().Ticks;
        using var helper = StartManagerUpdateHelper(stage, parent.Id, parentStart);
        await Task.Delay(500);
        Assert(!helper.HasExited && PendingManagerUpdateStore.HashFile(target) == oldHash, "updater waits for manager exit");
        await parent.StandardInput.WriteLineAsync("exit");
        await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert(helper.ExitCode == 0 && PendingManagerUpdateStore.HashFile(target) == newHash && PendingManagerUpdateStore.Read() is null,
            "verified manager update applied after exit");
        Console.WriteLine("PASS: manager updater waits for exit and replaces only the manager executable");

        File.Copy(oldExe, target, overwrite: true);
        PendingManagerUpdateStore.Write(pending with { TargetSha256 = new string('0', 64) });
        using var failedHelper = StartManagerUpdateHelper(stage, parent.Id, parentStart);
        await failedHelper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
        Assert(failedHelper.ExitCode != 0 && PendingManagerUpdateStore.HashFile(target) == oldHash && PendingManagerUpdateStore.Read()?.Failed == true,
            "changed target is preserved and update failure is recorded");
        Console.WriteLine("PASS: changed manager executable is preserved on update failure");

        var stale = Path.Combine(PendingManagerUpdateStore.DirectoryPath, "SillyTavernTray-v0.0.1.exe");
        var unrelated = Path.Combine(PendingManagerUpdateStore.DirectoryPath, "unrelated.exe");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(unrelated, "keep");
        PendingManagerUpdateStore.CleanupStaleFiles();
        Assert(!File.Exists(stale) && File.Exists(stage) && File.Exists(unrelated), "stale update cleanup is scoped");
        Console.WriteLine("PASS: stale manager packages are cleaned without touching pending or unrelated files");

        PendingManagerUpdateStore.Write(pending with { TargetSha256 = oldHash, Failed = false, Error = null });
        new AppSettings { InstallDirectory = work, StartServerOnLaunch = false, AutoUpdateManager = false }.Save();
        var launchedAt = DateTime.Now.AddSeconds(-1);
        var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--autostart");
        using var oldManager = Process.Start(start) ?? throw new Exception("old manager fixture not started");
        await oldManager.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntil(() => PendingManagerUpdateStore.Read() is null && PendingManagerUpdateStore.HashFile(target) == newHash,
            "pending manager update installed on startup");
        Process? restarted = null;
        try
        {
            for (var i = 0; i < 100 && restarted is null; i++)
            {
                foreach (var process in Process.GetProcessesByName("SillyTavernTray"))
                {
                    try
                    {
                        if (process.MainModule?.FileName.Equals(target, StringComparison.OrdinalIgnoreCase) == true && process.StartTime >= launchedAt)
                        { restarted = process; break; }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    process.Dispose();
                }
                if (restarted is null) await Task.Delay(100);
            }
            Assert(restarted is not null, "manager restarted after applying update");
            Console.WriteLine("PASS: pending update installs before startup and restarts only the isolated manager");
        }
        finally
        {
            if (restarted is not null)
            {
                try
                {
                    if (!restarted.HasExited && restarted.MainModule?.FileName.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
                    { restarted.Kill(); await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                restarted.Dispose();
            }
        }

        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(oldExe, target, overwrite: true); break; }
            catch (IOException) when (attempt < 20) { await Task.Delay(250); }
        }
        var replacement = Path.Combine(targetDir, "replacement.exe");
        var backup = Path.Combine(targetDir, "previous.exe");
        File.Copy(newExe, replacement, overwrite: true);
        var liveInfo = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true };
        liveInfo.ArgumentList.Add("--autostart");
        using var liveManager = Process.Start(liveInfo) ?? throw new Exception("live manager fixture not started");
        try
        {
            await Task.Delay(1500);
            Assert(!liveManager.HasExited, "live manager fixture running");
            try
            {
                File.Replace(replacement, target, backup, ignoreMetadataErrors: true);
                Assert(!liveManager.HasExited && PendingManagerUpdateStore.HashFile(target) == newHash && PendingManagerUpdateStore.HashFile(backup) == oldHash,
                    "running manager remains alive after replacement");
                Console.WriteLine("PASS: atomic on-disk replacement leaves the running manager active");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.WriteLine("SKIP: Windows prevented replacing a running executable: " + ex.GetType().Name); }
        }
        finally
        {
            if (!liveManager.HasExited && liveManager.MainModule?.FileName.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
            { liveManager.Kill(); await liveManager.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static Process StartManagerUpdateHelper(string executable, int parentId, long parentStart)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { ManagerUpdateInstaller.HelperArgument, parentId.ToString(), parentStart.ToString(), "0", "0" }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new Exception("manager update helper not started");
    }

    private sealed class FakeReleaseHandler(string json, string executable) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsoluteUri == ManagerUpdateService.LatestUrl)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            if (request.RequestUri?.Host == "github.com")
            {
                var stream = File.OpenRead(executable);
                var content = new StreamContent(stream);
                content.Headers.ContentLength = stream.Length;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static void VerifyTrayOnlyWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset);
                using var form = new MainForm(false, signal);
                Assert(!form.ShowInTaskbar, "window initially hidden from taskbar");
                form.Opacity = 0; // Exercise visibility without presenting a test window to the user.
                form.Show();
                Application.DoEvents();
                Assert(form.Visible && !form.ShowInTaskbar, "visible window hidden from taskbar");
                form.Hide();
                var restore = typeof(MainForm).GetMethod("RestoreWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new Exception("RestoreWindow missing");
                restore.Invoke(form, null);
                Application.DoEvents();
                Assert(form.Visible && !form.ShowInTaskbar, "restored window hidden from taskbar");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("Tray window test timed out");
        if (failure is not null) throw new Exception("Tray window test failed", failure);
    }
}
