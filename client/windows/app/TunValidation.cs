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
        using var adapter = new WintunAdapter(lease.InterfaceName, lease.AdapterId); lease.ObserveAdapter(adapter.AdapterId);
        await File.WriteAllTextAsync(Program.Option(args, "--report")!, JsonSerializer.Serialize(new { adapter = lease.AdapterId }));
        await Task.Delay(Timeout.Infinite); return 0;
    }
    private static async Task<string[]> TunIntegration(string folder, string expected)
    {
        var checks = new List<string>();
        var dnsBefore = DnsSnapshot();
        var profile = Store.Import(folder);
        var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, CaptureMode = "TUN", SystemProxy = false, HttpPort = Port(), SocksPort = Port(), Sites = new TunnelPolicy { Mode = "Tunnel" } };
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string curlPath = Path.Combine(windows, "System32", "curl.exe");
        string curlLaunch = Path.Combine(windows, Environment.Is64BitProcess ? "System32" : "Sysnative", "curl.exe");
        string lastCurlError = "";
        async Task<int> Curl()
        {
            var info = new ProcessStartInfo(curlLaunch) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "--noproxy", "*", "--max-time", "8", "-fsS", "-o", "NUL", "https://api.ipify.org/?format=json" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await Task.WhenAll(output, error); lastCurlError = (await error)[..Math.Min(700, (await error).Length)]; return process.ExitCode;
        }
        await using (var backend = new Backend())
        {
            await backend.ConnectAsync(settings);
            Assert(backend.State == "Connected" && backend.ExitIp == expected && NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name == TunLease.Name && x.OperationalStatus == OperationalStatus.Up), "TUN capture did not verify.");
            checks.Add("real Wintun adapter, native authenticated frontend and captured DNS/HTTPS verified");
            int baseline = await Curl(); Assert(baseline == 0, "External process baseline failed: exit=" + baseline + "; " + lastCurlError + "; " + backend.NativeTunDiagnostic);
            await SocksUdpEcho(settings.SocksPort); checks.Add("native SOCKS UDP relay traverses Xray and Hysteria");
            settings.Apps = new TunnelPolicy { Mode = "Tunnel", Entries = new() { new() { Kind = "App", Value = curlPath, Action = "Block" } } };
            await backend.ConnectAsync(settings);
            Assert(await Curl() != 0 && backend.State == "Connected", "App Block must block external process while private health remains connected.");
            checks.Add("real executable Block rule enforced without blocking private health");
            settings.Apps.Entries[0].Action = "Tunnel";
            settings.Sites = new TunnelPolicy { Mode = "Block", Entries = new() { new() { Kind = "Domain", Value = "api.ipify.org", Action = "Block" } } };
            await backend.ConnectAsync(settings);
            int appTunnel = await Curl(); Assert(appTunnel == 0, "Explicit app Tunnel must win over site Block: curl=" + appTunnel + "; " + lastCurlError + "; " + backend.NativeTunDiagnostic);
            checks.Add("explicit App Tunnel overrides a conflicting Site Block on an external process");
            await backend.StopAsync();
            for (int i = 0; i < 50 && NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name == TunLease.Name); i++) await Task.Delay(100);
            Assert(!NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name == TunLease.Name), "Owned TUN adapter remained after stop: " + string.Join("; ", NetworkInterface.GetAllNetworkInterfaces().Where(x => x.Name == TunLease.Name).Select(x => x.Id + " " + x.OperationalStatus)));
        }
        Assert(dnsBefore == DnsSnapshot(), "Physical-interface DNS changed.");
        checks.Add("normal stop removes owned adapter and routes; physical DNS remains unchanged");
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
                for (int i = 0; i < 100 && WintunAdapter.Exists(id); i++) await Task.Delay(100);
                Assert(!WintunAdapter.Exists(id), "Guard failed to remove adapter after parent crash.");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } TunLease.RecoverAbandoned(); }
        }
        checks.Add("independent recovery guard removes owned adapter after an abruptly killed parent");
        return checks.ToArray();
    }
    private static string DnsSnapshot() => string.Join("|", NetworkInterface.GetAllNetworkInterfaces().Where(x => !x.Name.StartsWith("PersonalVPN-", StringComparison.Ordinal)).OrderBy(x => x.Id).Select(x => x.Id + ":" + string.Join(",", x.GetIPProperties().DnsAddresses.Select(a => a.ToString()))));
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
