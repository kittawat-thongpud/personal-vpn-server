using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;
internal static partial class Tests
{
    internal static async Task<int> TunLeaseChild(string[] args)
    {
        string folder = Program.Option(args, "--folder")!; Store.SecureDirectory(folder);
        using var lease = await TunLease.Start(folder, CancellationToken.None);
        using var job = new ProcessJob();
        var settings = new Settings { CaptureMode = "TUN", Sites = new TunnelPolicy { Mode = "Tunnel" } };
        string path = Path.Combine(folder, "crash-tun.json");
        await File.WriteAllTextAsync(path, TunPlan.Compile(settings, new[] { "127.0.0.1" }, new[] { Backend.VerifyCore("xray.exe"), Backend.VerifyCore("hysteria.exe") }, lease.InterfaceName).ToJsonString());
        using var core = new OwnedCore(job, Backend.VerifyCore("sing-box.exe"), new[] { "run", "-c", path }, new SessionLog());
        await TunSupport.WaitForAdapter(lease, CancellationToken.None);
        string ready = Program.Option(args, "--report")!;
        await File.WriteAllTextAsync(ready + ".new", JsonSerializer.Serialize(new { adapter = lease.AdapterId }));
        File.Move(ready + ".new", ready); // Publish only after the writer has closed.
        await Task.Delay(Timeout.Infinite); return 0;
    }
    private static async Task<string[]> TunIntegration(string folder, string expected)
    {
        var checks = new List<string>();
        AdapterService.EnsureRunning();
        Guid persistentId = AdapterService.CurrentId();
        uint serviceId = AdapterService.ProcessId;
        var dnsBefore = DnsSnapshot();
        var proxyBefore = WindowsProxy.Read();
        var profile = Store.Import(folder);
        var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, CaptureMode = "TUN", SystemProxy = false, HttpPort = Port(), SocksPort = Port(), Sites = new TunnelPolicy { Mode = "Tunnel" } };
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string curlPath = Path.Combine(windows, "System32", "curl.exe");
        string curlLaunch = Path.Combine(windows, Environment.Is64BitProcess ? "System32" : "Sysnative", "curl.exe");
        string lastCurlError = "";
        async Task<int> Curl(string url = "https://api.ipify.org/?format=json")
        {
            var info = new ProcessStartInfo(curlLaunch) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--noproxy", "*", "--max-time", "8", "-fsS", "-o", "NUL", url }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await Task.WhenAll(output, error); lastCurlError = (await error)[..Math.Min(700, (await error).Length)]; return process.ExitCode;
        }
        await using (var backend = new Backend())
        {
            int connectionAttempt=0;
            async Task ConnectWithDiagnostics()
            {
                connectionAttempt++;
                try { await backend.ConnectAsync(settings); }
                catch (Exception error)
                {
                    string events=string.Join(" | ",backend.Log.Snapshot().TakeLast(12).Select(x=>x.Event+": "+x.Message));
                    throw new InvalidOperationException("TUN fixture connect #"+connectionAttempt+" ("+settings.CaptureMode+"): "+error.Message+"; native="+backend.NativeTunDiagnostic+"; events="+events,error);
                }
            }
            await ConnectWithDiagnostics();
            Assert(backend.State == "Connected" && backend.ExitIp == expected && NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name == TunLease.Name && x.OperationalStatus == OperationalStatus.Up), "TUN capture did not verify.");
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.Direct()), "TUN must suspend Windows proxy / PAC capture.");
            Assert(AdapterService.CurrentId() == persistentId, "TUN replaced the persistent device.");
            checks.Add("persistent Wintun adapter, authenticated frontend, captured DNS/HTTPS and suspended Windows proxy verified");
            int baseline = await Curl(); Assert(baseline == 0, "External process baseline failed: exit=" + baseline + "; " + lastCurlError + "; " + backend.NativeTunDiagnostic);
            Assert((await Dns.GetHostAddressesAsync("www.google.com")).All(x => x.AddressFamily == AddressFamily.InterNetwork), "IPv4-only DNS still advertises AAAA addresses.");
            Assert(await Curl("https://www.google.com/generate_204") == 0, "Default external curl failed Google HTTPS without a forced address family: " + lastCurlError);
            checks.Add("IPv4 DNS default suppresses unusable AAAA; external default curl reaches Google without -4");
            await SocksUdpEcho(settings.SocksPort); checks.Add("native SOCKS UDP relay traverses Xray and Hysteria");
            settings.Apps = new TunnelPolicy { Mode = "Tunnel", Entries = new() { new() { Kind = "App", Value = curlPath, Action = "Block" } } };
            await ConnectWithDiagnostics();
            int blockedCurl = await Curl();
            Assert(blockedCurl != 0 && backend.State == "Connected", "App Block result: curl=" + blockedCurl + "; state=" + backend.State + "; " + lastCurlError + "; " + backend.NativeTunDiagnostic);
            checks.Add("real executable Block rule enforced without blocking private health");
            settings.Apps.Entries[0].Action = "Tunnel";
            settings.Sites = new TunnelPolicy { Mode = "Block", Entries = new() { new() { Kind = "Domain", Value = "api.ipify.org", Action = "Block" } } };
            await ConnectWithDiagnostics();
            int appTunnel = await Curl(); Assert(appTunnel == 0, "Explicit app Tunnel must win over site Block: curl=" + appTunnel + "; " + lastCurlError + "; " + backend.NativeTunDiagnostic);
            checks.Add("explicit App Tunnel overrides a conflicting Site Block on an external process");
            await backend.StopAsync();
            Assert(AdapterService.CurrentId() == persistentId, "Disconnect replaced or removed the adapter.");
            Assert(!NetworkInterface.GetAllNetworkInterfaces().Single(x => x.Name == TunLease.Name).GetIPProperties().UnicastAddresses.Any(x => x.Address.ToString() == "172.28.255.1"), "Capture IP remained after stop.");
            settings.Apps = new TunnelPolicy { Mode = "Disabled" }; settings.Sites = new TunnelPolicy { Mode = "Tunnel" };
            for (int i = 0; i < 3; i++)
            {
                settings.CaptureMode = "Proxy"; await ConnectWithDiagnostics();
                Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.ForPort(settings.HttpPort)), "Proxy switch did not capture.");
                settings.CaptureMode = "TUN"; await ConnectWithDiagnostics();
                Assert(backend.State == "Connected" && await Curl() == 0, "Repeated Proxy to TUN switch lost Internet.");
                Assert(AdapterService.CurrentId() == persistentId && AdapterService.ProcessId == serviceId && NetworkInterface.GetAllNetworkInterfaces().Count(x => x.Name == TunLease.Name) == 1, "Mode switch duplicated the device.");
                if (i == 0) { backend.KillOwnedCoreForTest(); await Task.Delay(2000); await WaitConnected(backend, 70); Assert(AdapterService.CurrentId() == persistentId && await Curl() == 0, "Core recovery lost Internet or replaced the adapter."); }
                await backend.StopAsync();
                Assert(WindowsProxy.Matches(WindowsProxy.Read(), proxyBefore), "Mode switch failed to restore Windows proxy.");
            }
            WindowsProxy.Write(WindowsProxy.ForPort(settings.HttpPort)); // Simulate the stale loopback setting reported by the user.
            await ConnectWithDiagnostics();
            Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.Direct()) && await Curl() == 0, "Stale loopback proxy survived TUN capture.");
            await backend.StopAsync(); Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.Direct()), "Disconnect restored a dead owned proxy.");
            WindowsProxy.Write(proxyBefore);
        }
        Assert(dnsBefore == DnsSnapshot(), "Physical-interface DNS changed.");
        checks.Add("repeated Proxy / TUN / Disconnect switches and core crash recovery retain one GUID and working Internet; stale owned proxy removed; physical DNS unchanged");
        string leaseFolder = Path.Combine(Store.Root, "runtime", Guid.NewGuid().ToString("N")); Store.SecureDirectory(leaseFolder);
        string report = Path.Combine(leaseFolder, "child.json");
        var childInfo = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "--tun-lease-child", "--folder", leaseFolder, "--report", report }) childInfo.ArgumentList.Add(arg);
        using (var child = Process.Start(childInfo)!)
        {
            try
            {
                for (int i = 0; i < 300 && !File.Exists(report); i++) { Assert(!child.HasExited, "Adapter child failed."); await Task.Delay(100); }
                Assert(File.Exists(report), "Adapter child readiness timed out.");
                Guid id = JsonDocument.Parse(await File.ReadAllTextAsync(report)).RootElement.GetProperty("adapter").GetGuid();
                child.Kill(); await child.WaitForExitAsync();
                for (int i = 0; i < 300 && !File.Exists(Path.Combine(leaseFolder, "tun-reset-done")); i++) await Task.Delay(100);
                Assert(File.Exists(Path.Combine(leaseFolder, "tun-reset-done")), "Independent crash reset did not complete.");
                Assert(WintunAdapter.Exists(id) && id == persistentId, "Crash recovery deleted the persistent device.");
                Assert(!NetworkInterface.GetAllNetworkInterfaces().Single(x => x.Name == TunLease.Name).GetIPProperties().UnicastAddresses.Any(x => x.Address.ToString() == "172.28.255.1"), "Crash recovery left capture addresses.");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } TunLease.RecoverAbandoned(); }
        }
        checks.Add("independent recovery guard resets capture after abrupt parent death and retains the same adapter");
        return checks.ToArray();
    }
    private static string DnsSnapshot() => string.Join("|", NetworkInterface.GetAllNetworkInterfaces().Where(x => !x.Name.StartsWith("PersonalVPN", StringComparison.Ordinal)).OrderBy(x => x.Id).Select(x => x.Id + ":" + string.Join(",", x.GetIPProperties().DnsAddresses.Select(a => a.ToString()))));
    private static async Task SocksUdpEcho(int port)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)); var token = timeout.Token;
        using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int destination = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
        var echoTask = Task.Run(async () => { var request = await echo.ReceiveAsync(token); await echo.SendAsync(request.Buffer, request.RemoteEndPoint, token); }, token);
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, token); var stream = control.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token); byte[] response = new byte[2]; await stream.ReadExactlyAsync(response, token);
        Assert(response.SequenceEqual(new byte[] { 5, 0 }), "SOCKS authentication negotiation failed.");
        await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        Assert(header[1] == 0 && header[3] == 1, "SOCKS UDP association failed.");
        byte[] bind = new byte[6]; await stream.ReadExactlyAsync(bind, token);
        var address = new IPAddress(bind[..4]); if (address.Equals(IPAddress.Any)) address = IPAddress.Loopback;
        int relayPort = bind[4] * 256 + bind[5];
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("personal-vpn-udp-test");
        byte[] packet = new byte[10 + payload.Length]; packet[3] = 1; packet[4] = 127; packet[7] = 1; packet[8] = (byte)(destination >> 8); packet[9] = (byte)destination; payload.CopyTo(packet, 10);
        await udp.SendAsync(packet, new IPEndPoint(address, relayPort), token);
        var echoed = await udp.ReceiveAsync(token); Assert(echoed.Buffer.AsSpan().EndsWith(payload), "UDP echo payload did not return."); await echoTask;
    }
}
