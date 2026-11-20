#if PVN_WFP_LAB
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalVpn;

internal sealed class WfpPacketTrace : IDisposable
{
    private bool active, addedFilters;

    internal WfpPacketTrace(string directory, IReadOnlyDictionary<string, string> targets, IPAddress physical, IPAddress tunnel)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Packet recording is restricted to the disposable hosted runner.");
        if (Regex.IsMatch(Pktmon("filter", "list"), @"(?m)^\s*\d+\s")) throw new InvalidOperationException("Existing Packet Monitor filters are in use.");
        Directory.CreateDirectory(directory);
        var nics = new Dictionary<string, object>();
        foreach (var (label, ip) in new[] { ("physical", physical), ("wintun", tunnel) })
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().Single(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(ip)));
            nics[label] = new { nic.Name, nic.Description, nic.Id, mac = nic.GetPhysicalAddress().ToString() };
        }
        File.WriteAllText(Path.Combine(directory, "adapters.json"), JsonSerializer.Serialize(nics));
        File.WriteAllText(Path.Combine(directory, "pktmon-list.txt"), Pktmon("list"));
        try
        {
            foreach (var (protocol, value) in targets)
            {
                var endpoint = IPEndPoint.Parse(value);
                AddFilter(protocol, "-i", endpoint.Address.ToString(), "-t", protocol, "-p", endpoint.Port.ToString());
            }
            AddFilter("dns", "-t", "udp", "-p", "53");
            Pktmon("start", "--capture", "--comp", "nics", "--pkt-size", "104", "--file-name", Path.Combine(directory, "probe.etl"), "--file-size", "8");
            active = true;
        }
        catch { Dispose(); throw; }
    }

    private void AddFilter(string name, params string[] args)
    {
        name = "pvn-" + name;
        Pktmon(new[] { "filter", "add", name }.Concat(args).ToArray());
        addedFilters = true;
    }

    private static string Pktmon(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pktmon.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Pktmon command timed out: " + args[0]); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Pktmon {args[0]} failed ({process.ExitCode}): {output.Result} {error.Result}");
        return output.Result;
    }

    public void Dispose()
    {
        try { if (active) { active = false; Pktmon("stop"); } }
        finally { if (addedFilters) { Pktmon("filter", "remove"); addedFilters = false; } }
    }
}
#endif
