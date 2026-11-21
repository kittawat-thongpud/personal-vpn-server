#if PVN_WFP_LAB
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private sealed record WfpObservation(string Stage, string App, string Protocol, bool Reached, string SourcePath, bool? DriverExcluded, uint? WindowsDnsStatus = null);
    private sealed record PacketFlow(string Stage, string App, string Protocol, string Local, int Port, string Endpoint, string? WireToken);
    private sealed record PacketDns(string App, string Name, uint Status);

    private static async Task<object> NativeWfpMatrix(string root, string expected, string probeExecutable, string progress, string? packetDir)
    {
        const string https = "speed.cloudflare.com", stun = "stun.cloudflare.com";
        var tunnelIP = IPAddress.Parse("172.28.255.1");
        var targets = new Dictionary<string, string> {
            ["tcp"] = (await Dns.GetHostAddressesAsync(https, AddressFamily.InterNetwork)).First() + ":443",
            ["udp"] = (await Dns.GetHostAddressesAsync(stun, AddressFamily.InterNetwork)).First() + ":3478"
        };
        var probes = new[] { "bypass", "tunneled", "blocked" }.ToDictionary(role => role, role => Path.Combine(root, role + "-probe.exe"));
        foreach (var path in probes.Values) File.Copy(probeExecutable, path);
        var trace = new List<WfpObservation>();
        var flows = new List<PacketFlow>();
        var dns = new List<PacketDns>();
        var physical = new Dictionary<string, IPAddress>();
        var dnsNames = probes.Keys.ToDictionary(role => role, _ => "pvn-" + Guid.NewGuid().ToString("N")[..8] + ".example.com.");
        var positiveDnsNames = probes.Keys.Zip(new[] { "example.com.", "example.net.", "example.org." }).ToDictionary(p => p.First, p => p.Second);

        Task SavePacketManifest() => File.WriteAllTextAsync(Path.Combine(packetDir!, "manifest.json"), JsonSerializer.Serialize(new { flows, dns }));

        async Task<WfpObservation> RunProbe(string stage, string role, string protocol, WfpReferenceLab? driver = null, string? query = null)
        {
            var info = new ProcessStartInfo(probes[role]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in query != null ? new[] { protocol, "unused", query } : new[] { protocol, targets[protocol], https }) info.ArgumentList.Add(value);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try
            {
                bool? split = null;
                if (driver != null)
                {
                    for (int retry = 0; retry < 20; retry++)
                    {
                        try { split = driver.IsExcluded(process.Id); break; }
                        catch (Win32Exception) when (retry < 19 && !process.HasExited) { await Task.Delay(100); }
                    }
                }
                await process.StandardInput.WriteLineAsync("probe"); process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(protocol == "dns" ? 18 : 11));
                _ = await error;
                Assert(process.ExitCode == 0, $"{stage}: {role} {protocol} probe process failed.");
                using var result = JsonDocument.Parse(await output);
                var json = result.RootElement;
                bool reached = json.GetProperty("reached").GetBoolean();
                uint? dnsStatus = protocol == "dns" ? json.GetProperty("dnsStatus").GetUInt32() : null;
                IPAddress? local = json.TryGetProperty("localIP", out var value) && IPAddress.TryParse(value.GetString(), out var ip) ? ip : null;
                if (stage == "without_capture")
                {
                    Assert(reached && local != null && !local.Equals(tunnelIP) && !IPAddress.IsLoopback(local), $"Physical baseline unavailable for {protocol}.");
                    physical[protocol] = local!;
                }
                string path = local == null ? "unavailable" : local.Equals(tunnelIP) ? "wintun" : local.Equals(physical[protocol]) ? "physical" : "other";
                if (packetDir != null && stage != "without_capture" && !stage.StartsWith("client_", StringComparison.Ordinal))
                {
                    if (dnsStatus.HasValue && query != null) dns.Add(new PacketDns(role, query, dnsStatus.Value));
                    else if (local != null && json.TryGetProperty("localPort", out var port))
                    {
                        string? token = json.TryGetProperty("wireToken", out var wire) ? wire.GetString() : null;
                        flows.Add(new PacketFlow(stage, role, protocol, local.ToString(), port.GetInt32(), targets[protocol], token));
                    }
                    await SavePacketManifest();
                }
                var row = new WfpObservation(stage, role, protocol, reached, path, split, dnsStatus);
                trace.Add(row);
                await File.WriteAllTextAsync(progress, JsonSerializer.Serialize(trace));
                return row;
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }

        foreach (var protocol in targets.Keys) await RunProbe("without_capture", "bypass", protocol);
        Assert(physical["tcp"].Equals(physical["udp"]), "TCP and UDP start on different physical adapters; matrix is not comparable.");
        var profile = Store.Import(root);
        var originalProxy = WindowsProxy.Read();
        var adapter = AdapterService.CurrentId();
        var dnsBefore = DnsSnapshot();
        var settings = new Settings { Profiles = new() { profile }, SelectedId = profile.Id, CaptureMode = "TUN", SystemProxy = false, HttpPort = Port(), SocksPort = Port(),
            Apps = new TunnelPolicy { Mode = "Tunnel", Entries = probes.Select(p => new TunnelException { Kind = "App", Value = p.Value, Action = p.Key == "tunneled" ? "Tunnel" : "Block" }).ToList() } };
        await using var backend = new Backend();
        await backend.ConnectAsync(settings).WaitAsync(TimeSpan.FromSeconds(85));
        Assert(backend.State == "Connected" && backend.ExitIp == expected, "Owned Hysteria/Wintun fixture was not verified.");
        var initialCapture = backend.CapturePidForTest;
        Assert(initialCapture.HasValue, "No owned TUN core.");

        async Task Wave(string stage, bool enabled, WfpReferenceLab? driver = null)
        {
            foreach (var protocol in targets.Keys)
            {
                int trials = packetDir == null ? 1 : 3;
                foreach (var role in probes.Keys)
                    for (int trial = 0; trial < trials; trial++)
                    {
                        var row = await RunProbe(stage, role, protocol, driver);
                        bool outside = enabled && role == "bypass";
                        Assert(row.DriverExcluded is null || row.DriverExcluded == outside, $"{stage}: {role} kernel classification differs from the lab policy.");
                        Assert(row.Reached == (outside || role == "tunneled"), $"{stage}: {role} {protocol} reached={row.Reached}, source={row.SourcePath}.");
                        if (row.Reached) Assert(row.SourcePath == (outside ? "physical" : "wintun"), $"{stage}: {role} {protocol} selected {row.SourcePath}.");
                    }
            }
            Assert(backend.State == "Connected" && initialCapture == backend.CapturePidForTest, "Wintun restarted during the comparison.");
        }

        using (var packetTrace = packetDir == null ? null : new WfpPacketTrace(packetDir, targets, tunnelIP))
        {
            await Wave("capture_without_exclusion", false);
            using (var reference = new WfpReferenceLab())
            {
                reference.Configure(probes["bypass"], tunnelIP, physical["tcp"]);
                await Wave("capture_with_reference_exclusion", true, reference);
                if (packetTrace != null)
                    foreach (var role in probes.Keys)
                        foreach (string query in new[] { positiveDnsNames[role], dnsNames[role] })
                        {
                            var row = await RunProbe(query.StartsWith("pvn-", StringComparison.Ordinal) ? "windows_dns_negative" : "windows_dns_positive", role, "dns", reference, query);
                            Assert(row.DriverExcluded == (role == "bypass"), $"{role} DNS probe was classified incorrectly.");
                        }
                reference.Clear();
                await Wave("capture_after_exclusion_removal", false, reference);
            }
            await backend.StopAsync();
            foreach (var protocol in targets.Keys)
                for (int trial = 0; trial < (packetTrace == null ? 1 : 3); trial++)
                {
                    var row = await RunProbe("after_disconnect", "bypass", protocol);
                    Assert(row.Reached && row.SourcePath == "physical", $"Physical {protocol} egress did not recover after cleanup.");
                }
        }
        var preview = settings.Snapshot();
        preview.Apps.Entries.Single(entry => entry.Value == probes["bypass"]).Action = Policy.NativeBypassAction;
        await backend.ConnectAsync(preview).WaitAsync(TimeSpan.FromSeconds(85));
        Assert(backend.State == "Connected" && backend.Log.Snapshot().Any(entry => entry.Event == "backend.native_bypass_active"), "Client did not apply its Bypass VPN rule.");
        foreach (string protocol in targets.Keys)
            foreach (string app in probes.Keys)
            {
                var row = await RunProbe("client_applied", app, protocol);
                string expectedPath = app == "bypass" ? "physical" : "wintun";
                Assert(row.Reached == (app != "blocked") && row.SourcePath == expectedPath, $"Client {app} rule did not use the expected {protocol} route.");
            }
        await backend.ConnectAsync(settings).WaitAsync(TimeSpan.FromSeconds(85));
        Assert(backend.State == "Connected", "Client failed to reconnect after removing Bypass VPN.");
        foreach (string protocol in targets.Keys)
        {
            var row = await RunProbe("client_removed", "bypass", protocol);
            Assert(!row.Reached && row.SourcePath == "wintun", $"Removing Bypass VPN did not restore {protocol} capture.");
        }
        await backend.StopAsync();

        string guardFolder = Path.Combine(Store.Root, "runtime", Guid.NewGuid().ToString("N"));
        Store.SecureDirectory(guardFolder);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        using (var lease = await TunLease.Start(guardFolder, cancel.Token, new[] { probes["bypass"] }))
        {
            Assert(File.Exists(Path.Combine(guardFolder, "native-bypass.json")), "WFP owner was not journaled.");
            using var guard = Process.GetProcessById(lease.GuardId);
            guard.Kill();
            await guard.WaitForExitAsync(cancel.Token);
        }
        Assert(!File.Exists(Path.Combine(guardFolder, "native-bypass.json")), "Guard death left the WFP journal active.");
        using (var available = new WfpReferenceLab()) Assert(available.State == 3, "WFP could not acquire a clean device after guard recovery.");
        TunLease.CleanRuntimeFolder(guardFolder);
        Assert(AdapterService.CurrentId() == adapter && WindowsProxy.Matches(WindowsProxy.Read(), originalProxy), "Lab changed adapter ownership or proxy settings.");
        Assert(!NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(tunnelIP)), "Owned capture address remained after cleanup.");
        Assert(dnsBefore == DnsSnapshot(), "Physical DNS changed after cleanup.");
        return new { result = "PASS", driver = "signed reference callout", sameCaptureDuringWaves = true, appRulesAndRollbackVerified = true, clientRuleVerified = true, guardCrashRecovered = true, fixture = "IPv4 TCP/TLS and UDP/STUN", evidence = trace };
    }
}
#endif
