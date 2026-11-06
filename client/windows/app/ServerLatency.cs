using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal sealed record LatencyTarget(string Host, int Port)
{
    public string Key => Host + ":" + Port;
    public static LatencyTarget? From(ServerProfile profile, string protocol = "REALITY")
    {
        try
        {
            if (profile.Xray != null && (protocol == "REALITY" || profile.Hysteria == null))
            { var value = JsonNode.Parse(profile.Xray)!["outbounds"]![0]!["settings"]!["vnext"]![0]!; return new((string)value["address"]!, (int)value["port"]!); }
            var uri = new Uri("tcp://" + (string)JsonNode.Parse(profile.Hysteria!)!["server"]!);
            return new(uri.Host.Trim('[', ']'), uri.Port > 0 ? uri.Port : 443);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or System.Text.Json.JsonException or NullReferenceException) { return null; }
    }
}
internal sealed record LatencyResult(long? Milliseconds = null, DateTime Checked = default, bool Pending = false, string Reason = "Not checked", string Network = "")
{
    public string Label => Pending ? "…" : Milliseconds is long ms ? ms + " ms" : Checked == default ? "—" : Reason == "Timeout" ? "Timeout" : "Unavailable";
    public string Detail => "TCP connect · physical network\n" + (Checked == default ? "Not checked" : Checked.ToLocalTime().ToString("T")) + "\n" + Reason + "\nTCP reachability only; Hysteria 2 uses UDP.";
}

// A UI-only cache: two sockets at most, no routes, cores or recovery changes.
internal sealed class ServerLatency : IDisposable
{
    // Winsock IP_UNICAST_IF is 31; .NET has no named SocketOptionName member.
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;
    private readonly Dictionary<string, LatencyResult> cache = new();
    private readonly HashSet<string> pending = new();
    private readonly SemaphoreSlim workers = new(2);
    private CancellationTokenSource? foreground;
    private DateTime due;
    private LatencyTarget[] targets = Array.Empty<LatencyTarget>();
    public LatencyResult Get(LatencyTarget? target) => target != null && cache.TryGetValue(target.Key, out var result) ? result : new();
    public void Profiles(IEnumerable<ServerProfile> profiles, string protocol)
    {
        var next = profiles.Select(x => LatencyTarget.From(x,protocol)).OfType<LatencyTarget>().Distinct().ToArray();
        if (!next.SequenceEqual(targets)) due = DateTime.MinValue;
        targets = next;
        foreach (string key in cache.Keys.Where(x => targets.All(t => t.Key != x)).ToArray()) cache.Remove(key);
    }
    public void Tick(bool visible)
    {
        if (!visible)
        {
            if (foreground == null) return;
            foreground?.Cancel(); foreground?.Dispose(); foreground = null; pending.Clear();
            foreach (string key in cache.Keys.ToArray()) cache[key] = cache[key] with { Pending = false };
            return;
        }
        if (foreground == null) { foreground = new CancellationTokenSource(); due = DateTime.MinValue; }
        if (DateTime.UtcNow >= due) { due = DateTime.UtcNow.AddMinutes(5); Refresh(false); }
    }
    public void Refresh(bool force = true, LatencyTarget? only = null)
    {
        if (foreground == null) return;
        NetworkInterface[] links;
        try { links = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !(x.Name + " " + x.Description).Contains("VPN", StringComparison.OrdinalIgnoreCase) && !(x.Name + " " + x.Description).Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) && !(x.Name + " " + x.Description).Contains("Tailscale", StringComparison.OrdinalIgnoreCase) &&
            x.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))).ToArray(); }
        catch (NetworkInformationException) { links = Array.Empty<NetworkInterface>(); }
        string network = string.Join(";", links.Select(x => x.Id + ":" + string.Join(",", x.GetIPProperties().UnicastAddresses.Select(a => a.Address))).OrderBy(x => x));
        foreach (var target in only == null ? targets : new[] { only })
        {
            var previous = Get(target);
            if (pending.Contains(target.Key) || (!force && previous.Network == network && previous.Checked != default && DateTime.UtcNow - previous.Checked < TimeSpan.FromMinutes(5))) continue;
            pending.Add(target.Key); cache[target.Key] = previous with { Pending = true };
            _ = Measure(target, links, network, foreground.Token);
        }
    }
    private async Task Measure(LatencyTarget target, NetworkInterface[] links, string network, CancellationToken token)
    {
        try
        {
            await workers.WaitAsync(token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(3000);
                long? elapsed = null; string reason = "TCP connected";
                try
                {
                    var addresses = IPAddress.TryParse(target.Host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(target.Host, deadline.Token);
                    var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? throw new SocketException();
                    uint best = BestInterface(address);
                    var nic = links.FirstOrDefault(x => x.GetIPProperties().GetIPv4Properties().Index == best) ?? links.FirstOrDefault() ?? throw new SocketException();
                    int index = nic.GetIPProperties().GetIPv4Properties().Index;
                    var local = nic.GetIPProperties().UnicastAddresses.First(x => x.Address.AddressFamily == AddressFamily.InterNetwork).Address;
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface, IPAddress.HostToNetworkOrder(index));
                    socket.Bind(new IPEndPoint(local, 0)); var watch = Stopwatch.StartNew();
                    await socket.ConnectAsync(new IPEndPoint(address, target.Port), deadline.Token);
                    elapsed = Math.Max(1, watch.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { reason = "Timeout"; }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is SocketException or NetworkInformationException or InvalidOperationException or ArgumentException) { reason = "TCP unavailable"; }
                if (!token.IsCancellationRequested && targets.Any(t => t.Key == target.Key)) cache[target.Key] = new(elapsed, DateTime.UtcNow, false, reason, network);
            }
            finally { workers.Release(); }
        }
        catch (OperationCanceledException) { }
        finally { if (!token.IsCancellationRequested) pending.Remove(target.Key); }
    }
    private static uint BestInterface(IPAddress address)
    {
        var serialized = new IPEndPoint(address, 0).Serialize(); var bytes = new byte[serialized.Size];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = serialized[i];
        return GetBestInterfaceEx(bytes, out uint index) == 0 ? index : 0;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] address, out uint index);
    public void Dispose() { foreground?.Cancel(); foreground?.Dispose(); foreground = null; }
}
