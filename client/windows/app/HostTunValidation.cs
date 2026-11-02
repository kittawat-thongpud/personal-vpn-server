using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
namespace PersonalVpn;
internal static partial class Tests
{
    // Explicit operator diagnostic. No settings are saved and no negative
    // app/site rules are applied to a real workstation. Guards restore capture.
    public static async Task<int> HostTunTest(string[] args)
    {
        var checks = new List<string>(); var before = WindowsProxy.Read();
        Guid adapter = Guid.Empty;
        await using var backend = new Backend();
        try
        {
            AdapterService.EnsureRunning(); adapter = AdapterService.CurrentId();
            var profile = Store.Import(Program.Option(args, "--profile-dir")!);
            string expected = Program.Option(args, "--expected-exit")!;
            var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, CaptureMode = "TUN", HttpPort = Port(), SocksPort = Port(), Sites = new TunnelPolicy { Mode = "Tunnel" }, Apps = new TunnelPolicy { Mode = "Disabled" } };
            foreach (string protocol in new[] { "Hysteria 2", "REALITY" })
            {
                settings.Protocol = protocol; settings.CaptureMode = "TUN";
                await backend.ConnectAsync(settings);
                Assert(backend.State == "Connected" && backend.ExitIp == expected, protocol + " TUN failed to verify the VPS exit.");
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(15) };
                Assert(await Egress(client) == expected, "Independent HTTPS request did not exit through the VPS.");
                using var google = await client.GetAsync("https://www.google.com/generate_204"); google.EnsureSuccessStatusCode();
                Assert(WindowsProxy.Matches(WindowsProxy.Read(), WindowsProxy.Direct()), "Windows proxy was not suspended for TUN.");
                Assert(AdapterService.CurrentId() == adapter, "Adapter GUID changed.");
                checks.Add(protocol + ": captured HTTPS exit " + expected + "; Google reachable; same adapter GUID");
                foreach (string peer in new[] { "10.46.136.123", "10.46.136.125" })
                {
                    try { using var ping = new Ping(); var result = await ping.SendPingAsync(peer, 3000); checks.Add(protocol + ": mesh " + peer + " " + result.Status); }
                    catch (PingException) { checks.Add(protocol + ": mesh " + peer + " unavailable"); }
                }
                settings.CaptureMode = "Proxy"; await backend.ConnectAsync(settings);
                Assert(backend.State == "Connected", "TUN to Proxy switch failed.");
                settings.CaptureMode = "TUN"; await backend.ConnectAsync(settings);
                Assert(backend.State == "Connected" && backend.ExitIp == expected && AdapterService.CurrentId() == adapter, "Proxy to TUN switch failed.");
                await backend.StopAsync();
                Assert(!backend.Running && WindowsProxy.Matches(WindowsProxy.Read(), before), "Disconnect failed to restore proxy.");
                Assert(!NetworkInterface.GetAllNetworkInterfaces().Single(x => x.Name == TunLease.Name).GetIPProperties().UnicastAddresses.Any(x => x.Address.ToString() == "172.28.255.1"), "Disconnect left capture addresses.");
                checks.Add(protocol + ": TUN / Proxy / TUN / Disconnect passed; proxy restored; device retained");
            }
            return Report(args, new { result = "PASS", checks, adapter }, 0);
        }
        catch (Exception error)
        {
            return Report(args, new { result = "FAIL", checks, error = error.GetType().Name, reason = error is InvalidOperationException ? error.Message : "Host capture verification failed.", events = backend.Log.Snapshot().TakeLast(15) }, 1);
        }
        finally { await backend.StopAsync(); }
    }
}
