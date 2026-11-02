using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PersonalVpn;

public static class Program
{
    [STAThread]
    public static int Main(string[] arguments)
    {
        if (arguments.Contains("--proxy-guard"))
        {
            try { return SystemProxyLease.Watch(Option(arguments, "--proxy-guard")!).GetAwaiter().GetResult(); }
            catch { return 1; } // Recovery record remains for the next application startup.
        }
        if (arguments.Contains("--proxy-lease-child")) return Tests.LeaseChild(arguments);
        if (arguments.Contains("--self-test")) return Tests.SelfTest(arguments);
        if (arguments.Contains("--smoke-test")) return Tests.Smoke(arguments).GetAwaiter().GetResult();
        if (arguments.Contains("--system-proxy-test")) return Tests.ProxyTest(arguments).GetAwaiter().GetResult();
        if (arguments.Contains("--local-proxy-integration")) return Tests.LocalProxyIntegration(arguments).GetAwaiter().GetResult();
        if (arguments.Contains("--probe-protocol")) return Tests.ProtocolProbe(arguments).GetAwaiter().GetResult();
        using var mutex = new Mutex(true, @"Local\PersonalVPN-" + Environment.UserName, out bool created);
        if (!created) { MessageBox.Show("Personal VPN is already running. Restore its window from the taskbar.", "Personal VPN"); return 0; }
        try
        {
            SystemProxyLease.RecoverAbandoned();
            var settings = Store.Load();
            if (arguments.Contains("--connect-hysteria"))
            {
                settings.Protocol = "Hysteria 2"; settings.ChainReality = false;
                settings.SystemProxy = true; settings.Traffic = "Global"; Store.Save(settings);
            }
            string? importFolder = Option(arguments, "--import-folder");
            if (importFolder != null)
            {
                var profile = Store.Import(importFolder);
                if (!settings.Profiles.Any(x => x.Xray == profile.Xray && x.Hysteria == profile.Hysteria))
                {
                    settings.Profiles.Add(profile); settings.SelectedId = profile.Id; Store.Save(settings);
                }
            }
            // A previous crash may leave plaintext runtime configs. Only our fixed runtime child is cleaned.
            string runtime = Path.GetFullPath(Path.Combine(Store.Root, "runtime"));
            if (Path.GetDirectoryName(runtime) != Path.GetFullPath(Store.Root)) throw new InvalidOperationException("Invalid application runtime path.");
            if (Directory.Exists(runtime)) Directory.Delete(runtime, true);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, args) =>
            {
                // No raw exception text or credentials in persistent logs / dialogs.
                MessageBox.Show("The client encountered an unexpected error (" + args.Exception.GetType().Name + "). Close and reopen it before continuing.", "Personal VPN");
                // Exit releases the Windows job and terminates only owned VPN cores.
                Environment.Exit(1);
            };
            return app.Run(new MainWindow(settings, arguments.Contains("--connect") || arguments.Contains("--connect-hysteria")));
        }
        catch (Exception error)
        {
            MessageBox.Show(error is InvalidOperationException or InvalidDataException ? error.Message : "Startup failed (" + error.GetType().Name + "). Existing settings were preserved.", "Personal VPN");
            return 1;
        }
    }
    public static string? Option(string[] args, string key)
    {
        int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal static class Tests
{
    public static int LeaseChild(string[] args)
    {
        try
        {
            using var lease = new SystemProxyLease(int.Parse(Program.Option(args, "--port")!), new SessionLog());
            Thread.Sleep(Timeout.Infinite); return 0;
        }
        catch { return 1; }
    }
    private static void Assert(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static int Report(string[] args, object report, int result)
    {
        string json = JsonSerializer.Serialize(report, Store.JsonOptions);
        string? path = Program.Option(args, "--report");
        if (path != null) File.WriteAllText(path, json);
        Console.WriteLine(json); return result;
    }
    public static int SelfTest(string[] args)
    {
        var passed = new List<string>();
        try
        {
            var log = new SessionLog();
            for (int i = 0; i < 5000; i++) { log.Add("INFO", "backend.retry", i + new string('x', 10000)); log.Add("INFO", "ui.click", "ignored"); }
            Assert(log.Snapshot().Length == 1000 && log.Bytes <= 1048576, "Log bounds failed.");
            Assert(log.Snapshot().All(x => x.Message.Length <= 512 && x.Event == "backend.retry"), "Backend-only log filter / truncation failed.");
            log.Clear(); Assert(log.Snapshot().Length == 0 && log.Bytes == 0, "Clear failed."); passed.Add("backend log bounds, filtering, clear");
            var meter = new TrafficMeter(); meter.Reset(); meter.Update(100, 200); meter.Update(150, 260);
            meter.BeginCore(); meter.Update(10, 20);
            Assert(meter.Snapshot.Upload == 160 && meter.Snapshot.Download == 280 && meter.Snapshot.UploadRate >= 0, "Traffic counter reconnect accumulation failed.");
            for (int i = 10; i < 110; i++) meter.Update(i, i * 2);
            Assert(meter.History.Length == 60, "Traffic history is not bounded."); meter.Unavailable();
            Assert(!meter.Snapshot.Available && meter.Snapshot.UploadRate == 0 && meter.Snapshot.Upload > 0, "Unavailable traffic must not show a fake live rate.");
            bool malformedRejected = false; try { _ = CoreTraffic.Decode(new byte[] { 10, 100, 1 }); } catch (InvalidDataException) { malformedRejected = true; }
            Assert(malformedRejected, "Malformed traffic frame accepted."); passed.Add("traffic totals survive core restart; bounded history; unavailable state; malformed frame rejection");
            var settings = new Settings { Websites = new CategoryRule { Mode = "Bypass", Entries = new() { "example.com" } }, Networks = new CategoryRule { Mode = "Block", Entries = new() { "203.0.113.0/24", "2001:db8::/32" } } };
            Policy.Validate(settings.Websites, true); Policy.Validate(settings.Networks, false);
            var rules = Policy.Compile(settings);
            Assert((string?)rules[0]?["outboundTag"] == "tunnel" && (string?)rules[1]?["outboundTag"] == "block" && (string?)rules[2]?["outboundTag"] == "direct", "Routing precedence failed.");
            Assert((string?)rules.Last()?["outboundTag"] == "tunnel", "Global default failed.");
            settings.Traffic = "Rules"; Assert((string?)Policy.Compile(settings).Last()?["outboundTag"] == "direct", "Rules default failed.");
            settings.Websites.Mode = "Disabled"; Assert(Policy.Compile(settings).Count == 3, "Disabled rule must be omitted without deleting entries."); passed.Add("health bypasses user policy; Block priority; domain/IP defaults; disabled rules");
            foreach (string invalid in new[] { "https://example.com/", "*.example.com", "127.0.0.1", "bad..com", "x;cmd" })
            {
                bool rejected = false;
                try { Policy.Validate(new CategoryRule { Entries = new() { invalid } }, true); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "Invalid domain rule accepted.");
            }
            passed.Add("invalid domain input rejected");
            string temp = Path.Combine(Path.GetTempPath(), "personal-vpn-self-test-" + Guid.NewGuid().ToString("N"));
            Store.SecureDirectory(temp);
            try
            {
                File.WriteAllText(Path.Combine(temp, "hysteria.json"), """{"server":"127.0.0.1:443","auth":"test-secret","tls":{"pinSHA256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"http":{"listen":"0.0.0.0:80"},"log":{"output":"secret.log"}}""");
                var profile = Store.Import(temp);
                var imported = JsonNode.Parse(profile.Hysteria!)!;
                Assert(imported["http"] == null && imported["log"] == null && (bool?)imported["tls"]?["insecure"] == true && (string?)imported["tls"]?["pinSHA256"] != null, "Unsafe import / lost certificate pin.");
                passed.Add("import allowlist and full-certificate pin retained");
            }
            finally
            {
                if (Path.GetFileName(temp).StartsWith("personal-vpn-self-test-", StringComparison.Ordinal) && Path.GetDirectoryName(temp) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(temp, true);
            }
            _ = Backend.VerifyCore("xray.exe"); _ = Backend.VerifyCore("hysteria.exe"); passed.Add("bundled native core hashes");
            Assert(!WindowsProxy.Matches(new ProxySnapshot(2, "other", "", ""), WindowsProxy.ForPort(17890)), "Foreign proxy must not be owned.");
            _ = WindowsProxy.Read(); passed.Add("WinINet native proxy query and ownership comparison");
            _ = Application.Current ?? new Application();
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PersonalVPN;component/Theme.xaml", UriKind.Relative) });
            foreach (var widget in new System.Windows.Controls.Control[] { new System.Windows.Controls.ComboBox(), new System.Windows.Controls.Primitives.ScrollBar(), new System.Windows.Controls.Slider(), new System.Windows.Controls.TextBox(), new System.Windows.Controls.MenuItem(), new System.Windows.Controls.CheckBox() })
            {
                // Standalone controls have no window to resolve implicit styles during layout.
                widget.Style = (Style)Application.Current.FindResource(widget.GetType());
                widget.ApplyTemplate();
                Assert(widget.Template != null && System.Windows.Media.VisualTreeHelper.GetChildrenCount(widget) > 0, "Dark widget template did not load: " + widget.GetType().Name);
            }
            passed.Add("dark ComboBox, ScrollBar, Slider, TextBox, MenuItem and CheckBox templates load");
            return Report(args, new { result = "PASS", checks = passed }, 0);
        }
        catch (Exception error) { return Report(args, new { result = "FAIL", checks = passed, error = error.GetType().Name, reason = error.Message }, 1); }
    }
    private static async Task WaitConnected(Backend backend, int seconds = 90)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed.TotalSeconds < seconds)
        {
            if (backend.State == "Connected") return;
            await Task.Delay(250);
        }
        throw new InvalidOperationException("No verified Connected state before timeout.");
    }
    private static HttpClient Through(int port) => new(new SocketsHttpHandler { Proxy = new WebProxy("http://127.0.0.1:" + port), UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero }) { Timeout = TimeSpan.FromSeconds(20) };
    private static async Task<string> Egress(HttpClient client)
    {
        string json = await client.GetStringAsync("https://api.ipify.org/?format=json"); return JsonNode.Parse(json)!["ip"]!.GetValue<string>();
    }
    public static async Task<int> Smoke(string[] args)
    {
        var passed = new List<string>();
        await using var backend = new Backend();
        try
        {
            var profile = Store.Import(Program.Option(args, "--profile-dir") ?? throw new InvalidOperationException("Profile directory required."));
            string expected = Program.Option(args, "--expected-exit") ?? throw new InvalidOperationException("Expected exit required.");
            var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, HttpPort = Port(), SocksPort = Port(), SystemProxy = false };
            await backend.StartAsync(settings); await WaitConnected(backend);
            Assert(backend.ExitIp == expected, "Health exit mismatch.");
            using var client = Through(settings.HttpPort);
            var parallel = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Egress(client)));
            Assert(parallel.All(x => x == expected), "Parallel HTTP proxy exit mismatch."); passed.Add("Hysteria authenticated exit; HTTP proxy; 4 parallel requests");
            backend.KillOwnedCoreForTest();
            await Task.Delay(2000); await WaitConnected(backend);
            Assert(await Egress(client) == expected && backend.Log.Snapshot().Any(x => x.Event == "backend.retry"), "Core crash recovery failed."); passed.Add("owned core kill → retry → reverified connection");
            // Explicit route tests use a literal loopback HTTP fixture; never mutate host routing.
            var fixture = new TcpListener(IPAddress.Loopback, 0); fixture.Start();
            int fixturePort = ((IPEndPoint)fixture.LocalEndpoint).Port;
            var fixtureTask = Task.Run(async () =>
            {
                using var socket = await fixture.AcceptTcpClientAsync();
                using var stream = socket.GetStream(); var bytes = new byte[4096]; _ = await stream.ReadAsync(bytes);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nLOCAL"));
            });
            try
            {
                settings.Networks = new CategoryRule { Mode = "Bypass", Entries = new() { "127.0.0.1" } };
                await backend.StartAsync(settings); await WaitConnected(backend);
                Assert(await client.GetStringAsync("http://127.0.0.1:" + fixturePort) == "LOCAL", "Literal IP bypass failed.");
                await fixtureTask.WaitAsync(TimeSpan.FromSeconds(10)); passed.Add("explicit IP Bypass reaches local fixture through HTTP listener");
                settings.Networks.Mode = "Block"; await backend.StartAsync(settings); await WaitConnected(backend);
                bool blocked = false;
                try { _ = await client.GetStringAsync("http://127.0.0.1:" + fixturePort); } catch (HttpRequestException) { blocked = true; } catch (TaskCanceledException) { blocked = true; }
                Assert(blocked && backend.ExitIp == expected, "IP Block or policy-independent health failed."); passed.Add("IP Block enforced while dedicated tunnel health remains verified");
            }
            finally { fixture.Stop(); }
            settings.Networks.Mode = "Disabled";
            await backend.StopAsync();
            Assert(!backend.Running && backend.State == "Disconnected", "Stop failed.");
            using (var socket = new TcpClient())
            {
                bool closed = false; try { await socket.ConnectAsync(IPAddress.Loopback, settings.HttpPort); } catch (SocketException) { closed = true; }
                Assert(closed, "Owned proxy listener survived stop.");
            }
            passed.Add("disconnect closes owned listener");
            var badProfile = Store.Import(Program.Option(args, "--profile-dir")!);
            var bad = JsonNode.Parse(badProfile.Hysteria!)!; bad["tls"]!["pinSHA256"] = new string('0', 64); badProfile.Hysteria = bad.ToJsonString();
            var negative = settings.Snapshot(); negative.Profiles = new() { badProfile }; negative.SelectedId = badProfile.Id;
            await backend.StartAsync(negative); await Task.Delay(12000);
            Assert(backend.State != "Connected", "Wrong certificate pin was accepted."); await backend.StopAsync(); passed.Add("wrong Hysteria certificate pin never Connected");
            settings.Protocol = "REALITY"; settings.ChainReality = true;
            await backend.StartAsync(settings); await WaitConnected(backend);
            Assert(await Egress(client) == expected, "Chained REALITY exit mismatch."); passed.Add("REALITY via existing HTTP proxy verifies VPS exit");
            await backend.StopAsync();
            backend.Log.Clear(); Assert(backend.Log.Snapshot().Length == 0, "Backend log Clear failed."); passed.Add("live backend log Clear empties session");
            return Report(args, new { result = "PASS", checks = passed, expected_exit = expected, system_network_changes = false }, 0);
        }
        catch (Exception error) { return Report(args, new { result = "FAIL", checks = passed, error = error.GetType().Name, system_network_changes = false }, 1); }
    }
    public static async Task<int> ProxyTest(string[] args)
    {
        var passed = new List<string>();
        var before = WindowsProxy.Read();
        await using var backend = new Backend();
        Process? child = null;
        try
        {
            var profile = Store.Import(Program.Option(args, "--profile-dir") ?? throw new InvalidOperationException("Profile directory required."));
            string expected = Program.Option(args, "--expected-exit")!;
            var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, HttpPort = Port(), SocksPort = Port(), SystemProxy = true };
            await backend.ConnectProxyAsync(settings);
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.ForPort(settings.HttpPort)), "System proxy was not captured.");
            passed.Add("one Connect waits for tunnel verification and Windows proxy capture");
#pragma warning disable SYSLIB0014
            var systemProxy = WebRequest.GetSystemWebProxy();
#pragma warning restore SYSLIB0014
            Assert(systemProxy.GetProxy(new Uri("https://api.ipify.org"))!.Port == settings.HttpPort, "Windows proxy-aware client still uses another proxy.");
            using (var client = new HttpClient(new SocketsHttpHandler { Proxy = systemProxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(20) })
            {
                Assert(await Egress(client) == expected, "System proxy HTTPS exit mismatch.");
                passed.Add("Windows system proxy client verifies VPS HTTPS exit");
                foreach (string url in new[] { "https://www.google.com/generate_204", "https://www.wikipedia.org/" })
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    // An HTTP error still proves authenticated remote reachability; distinguish site policy from tunnel failure.
                    passed.Add(new Uri(url).Host + " HTTPS response: " + (int)response.StatusCode);
                }
            }
            passed.Add("Windows system proxy → Hysteria VPS exit; Google and Wikipedia HTTPS");
            for (int i = 0; i < 30 && (!backend.Traffic.Snapshot.Available || backend.Traffic.Snapshot.Download == 0); i++) await Task.Delay(200);
            Assert(backend.Traffic.Snapshot.Available && backend.Traffic.Snapshot.Upload > 0 && backend.Traffic.Snapshot.Download > 0, "Native tunnel byte counters did not advance after HTTPS traffic.");
            long uploadBeforeRestart = backend.Traffic.Snapshot.Upload, downloadBeforeRestart = backend.Traffic.Snapshot.Download;
            passed.Add("live native tunnel upload / download counters advance after real HTTPS");
            int routerBeforeReclaim = backend.RouterPidForTest;
            WindowsProxy.Write(before); // Simulate another app selecting its own proxy.
            await Task.Delay(1800);
            Assert(backend.State == "Proxy conflict", "External proxy change was not detected within two seconds.");
            await backend.ReclaimProxyAsync();
            Assert(backend.RouterPidForTest == routerBeforeReclaim && backend.State == "Connected" && backend.ExitIp == expected, "Proxy reclaim restarted the tunnel or failed verification.");
            passed.Add("proxy conflict detected in one second; explicit reclaim verified without core restart");
            backend.KillOwnedCoreForTest(); await Task.Delay(2000); await WaitConnected(backend);
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.ForPort(settings.HttpPort)), "Capture lost during core restart.");
            Assert(backend.Traffic.Snapshot.Upload >= uploadBeforeRestart && backend.Traffic.Snapshot.Download >= downloadBeforeRestart, "Traffic session totals reset after core restart.");
            passed.Add("system proxy retained across core crash / reconnect");
            await backend.StopAsync(); Assert(WindowsProxy.Matches(WindowsProxy.Read(), before), "Disconnect did not restore previous proxy.");
            passed.Add("disconnect restores exact previous manual / PAC / auto-detect snapshot");
            settings.SystemProxy = false;
            await backend.StartAsync(settings); await WaitConnected(backend);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--proxy-lease-child"); start.ArgumentList.Add("--port"); start.ArgumentList.Add(settings.HttpPort.ToString());
            child = Process.Start(start)!;
            for (int i = 0; i < 60 && !WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.ForPort(settings.HttpPort)); i++) await Task.Delay(250);
            Assert(!child.HasExited && WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.ForPort(settings.HttpPort)), "Crash test child did not capture proxy.");
            child.Kill(); await child.WaitForExitAsync();
            for (int i = 0; i < 60 && !WindowsProxy.Matches(WindowsProxy.Read(), before); i++) await Task.Delay(250);
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), before), "Watchdog did not restore after owner crash.");
            passed.Add("separate watchdog restores previous proxy after owner process is killed");
            using (var lease = new SystemProxyLease(settings.HttpPort, backend.Log)) WindowsProxy.Write(before);
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), before), "External proxy change was overwritten.");
            passed.Add("external proxy choice retained on lease disposal");
            await backend.StopAsync();
            return Report(args, new { result = "PASS", checks = passed, previous_proxy_restored = WindowsProxy.Matches(WindowsProxy.Read(), before) }, 0);
        }
        catch (Exception error)
        {
            return Report(args, new { result = "FAIL", checks = passed, error = error.GetType().Name, http_status = (error as HttpRequestException)?.StatusCode, reason = error is InvalidOperationException ? error.Message : "System proxy integration failed." }, 1);
        }
        finally
        {
            if (child != null) { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } child.Dispose(); }
            await backend.StopAsync(); SystemProxyLease.RecoverAbandoned();
        }
    }
    public static async Task<int> ProtocolProbe(string[] args)
    {
        await using var backend = new Backend();
        var profile = Store.Import(Program.Option(args, "--profile-dir") ?? throw new InvalidOperationException("Profile directory required."));
        var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, SystemProxy = false, ChainReality = args.Contains("--chain") };
        var result = await backend.TestProtocolAsync(settings, Program.Option(args, "--protocol") ?? "REALITY");
        return Report(args, result, result.Success ? 0 : 2);
    }
    public static async Task<int> LocalProxyIntegration(string[] args)
    {
        // Dedicated CI runner only: temporary local server credentials; no real device profiles.
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "personal-vpn-integration-" + Guid.NewGuid().ToString("N")));
        Store.SecureDirectory(root);
        using var job = new ProcessJob();
        OwnedCore? server = null;
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
            string certPath = Path.Combine(root, "tls.crt"), keyPath = Path.Combine(root, "tls.key");
            await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            int port = Port(); string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string serverConfig = Path.Combine(root, "server.json");
            await File.WriteAllTextAsync(serverConfig, JsonSerializer.Serialize(new
            {
                listen = "127.0.0.1:" + port, tls = new { cert = certPath, key = keyPath }, auth = new { type = "password", password }
            }));
            server = new OwnedCore(job, Backend.VerifyCore("hysteria.exe"), new[] { "server", "-c", serverConfig }, new SessionLog());
            await File.WriteAllTextAsync(Path.Combine(root, "hysteria.json"), JsonSerializer.Serialize(new
            {
                server = "127.0.0.1:" + port, auth = password, tls = new { sni = "localhost", insecure = true, pinSHA256 = certificate.GetCertHashString(HashAlgorithmName.SHA256) }
            }));
            using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
            string expected = JsonNode.Parse(await direct.GetStringAsync("https://api.ipify.org/?format=json"))!["ip"]!.GetValue<string>();
            return await ProxyTest(new[] { "--profile-dir", root, "--expected-exit", expected, "--report", Program.Option(args, "--report") ?? Path.Combine(root, "report.json") });
        }
        finally
        {
            server?.Dispose();
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("personal-vpn-integration-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid integration cleanup path.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
