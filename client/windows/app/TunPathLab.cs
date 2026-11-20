using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal static partial class Tests
{
    private static async Task<object[]> TunPathLab(string profileFolder, string expected)
    {
        const string httpsHost = "speed.cloudflare.com", stunHost = "stun.cloudflare.com";
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string curlPath = Path.Combine(windows, "System32", "curl.exe");
        string curlLaunch = Path.Combine(windows, Environment.Is64BitProcess ? "System32" : "Sysnative", "curl.exe");
        var profile = Store.Import(profileFolder);
        var ip = (await Dns.GetHostAddressesAsync(httpsHost, AddressFamily.InterNetwork)).First();
        var stun = (await Dns.GetHostAddressesAsync(stunHost, AddressFamily.InterNetwork)).First();
        var proxyBefore = WindowsProxy.Read();
        var adapter = AdapterService.CurrentId();
        var result = new List<object>();
        foreach (string mode in new[] { "os_default", "mixed", "system" })
        {
            await using var backend = new Backend(mode == "os_default" ? null : mode);
            int? capturePid = null;
            if (mode != "os_default")
            {
                var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, CaptureMode = "TUN", SystemProxy = false, HttpPort = Port(), SocksPort = Port() };
                settings.Apps = new TunnelPolicy { Mode = "Tunnel", Entries = new() { new() { Kind = "App", Value = curlPath, Action = "Bypass" } } };
                // The tested HTTPS IP is blocked for general captured callers;
                // only the explicit curl rule may reach it. Health uses separate hosts.
                settings.Sites = new TunnelPolicy { Mode = "Tunnel", Entries = new() {
                    new() { Kind = "IP", Value = ip.ToString(), Action = "Block" },
                    new() { Kind = "IP", Value = stun.ToString(), Action = "Bypass" }
                } };
                await backend.ConnectAsync(settings).WaitAsync(TimeSpan.FromSeconds(85));
                Assert(backend.State == "Connected" && backend.ExitIp == expected && AdapterService.CurrentId() == adapter, $"{mode} did not establish verified TUN capture.");
                capturePid = backend.CapturePidForTest;
            }
            long beforeDns = Stopwatch.GetTimestamp();
            _ = await Dns.GetHostAddressesAsync(httpsHost, AddressFamily.InterNetwork);
            double dnsMs = Stopwatch.GetElapsedTime(beforeDns).TotalMilliseconds;
            var tcp = new List<object>();
            for (int i = 0; i < 5; i++)
            {
                var start = new ProcessStartInfo(curlLaunch) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--noproxy", "*", "--max-time", "8", "--resolve", $"{httpsHost}:443:{ip}", "-fsS", "--write-out", "\n%{time_connect} %{time_appconnect} %{time_starttransfer} %{time_total}", $"https://{httpsHost}/cdn-cgi/trace" }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                _ = await error;
                var lines = (await output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                Assert(process.ExitCode == 0 && lines.Length >= 2 && lines.Contains("ip=" + expected), $"{mode} HTTPS probe failed or unexpected egress; curl exit {process.ExitCode}.");
                double[] samples = lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(value => double.Parse(value, CultureInfo.InvariantCulture) * 1000).ToArray();
                tcp.Add(new { connectMs = samples[0], tlsReadyMs = samples[1], headersMs = samples[2], totalMs = samples[3], edge = lines.FirstOrDefault(l => l.StartsWith("colo=", StringComparison.Ordinal))?[5..] });
            }
            var udp = new List<double?>();
            using (var socket = new UdpClient(AddressFamily.InterNetwork))
            {
                socket.Connect(new IPEndPoint(stun, 3478));
                for (int i = 0; i < 8; i++)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var request = RandomNumberGenerator.GetBytes(20);
                    new byte[] { 0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42 }.CopyTo(request, 0);
                    long before = Stopwatch.GetTimestamp();
                    double? rtt = null;
                    try
                    {
                        await socket.SendAsync(request, deadline.Token);
                        while (true)
                        {
                            var reply = (await socket.ReceiveAsync(deadline.Token)).Buffer;
                            if (reply.Length < 20 || reply[0] != 1 || reply[1] != 1 || !reply.AsSpan(4, 16).SequenceEqual(request.AsSpan(4, 16))) continue;
                            rtt = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                    catch (SocketException) { } // Public STUN may be unavailable on an isolated hosted runner.
                    udp.Add(rtt);
                }
            }
            bool stableCapture = capturePid != null && backend.State == "Connected" && backend.CapturePidForTest == capturePid;
            await backend.StopAsync();
            var events = backend.Log.Snapshot();
            var native = events.Where(e => e.Event == "backend.path_timing_native").Select(e => e.Message.Split(' '))
                .GroupBy(x => x[0]).ToDictionary(g => g.Key, g => g.Select(x => double.Parse(x[1], CultureInfo.InvariantCulture) / 1_000_000).OrderBy(x => x).ToArray());
            bool verified = mode == "os_default" || (stableCapture && native.ContainsKey("policy_tcp") && native.ContainsKey("socket_dial"));
            Assert(mode != "mixed" || verified, $"{mode} did not prove a stable captured Direct flow; observed stages: {string.Join(',', native.Keys)}.");
            // The supported stack must prove its path. A candidate without the
            // same proof is a recorded negative outcome, not a performance win.
            result.Add(new { mode, verified, issue = verified ? null : "Captured Direct path was not verified; do not compare timings.",
                resolverLookupMs = dnsMs, https = tcp, stunRttMs = udp, nativeTimingMs = native,
                connectionSetup = events.Where(e => e.Event == "backend.path_timing").Select(e => e.Message).ToArray() });
            Assert(AdapterService.CurrentId() == adapter && WindowsProxy.Matches(WindowsProxy.Read(), proxyBefore), "Path lab did not restore capture settings or changed the adapter.");
        }
        return result.ToArray();
    }
}
