using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;
public static class TunSupport
{
    public static IEnumerable<string> MeshPrefixes()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => (x.Name + " " + x.Description).Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) || (x.Name + " " + x.Description).Contains("Tailscale", StringComparison.OrdinalIgnoreCase)))
        foreach (var address in nic.GetIPProperties().UnicastAddresses)
        {
            byte[] bytes = address.Address.GetAddressBytes(); int prefix = address.PrefixLength;
            if (prefix <= 0 || address.Address.IsIPv6LinkLocal) continue;
            for (int i = 0; i < bytes.Length; i++) { int bits = Math.Clamp(prefix - i * 8, 0, 8); bytes[i] &= (byte)(0xff << (8 - bits)); }
            yield return new IPAddress(bytes) + "/" + prefix;
        }
    }
    public static string EndpointHost(Settings settings)
    {
        if (settings.Protocol == "REALITY") return JsonNode.Parse(settings.Selected.Xray!)!["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]!.GetValue<string>();
        return new Uri("tcp://" + JsonNode.Parse(settings.Selected.Hysteria!)!["server"]!.GetValue<string>()).Host.Trim('[', ']');
    }
    public static async Task<IPAddress[]> Resolve(Settings settings, CancellationToken token)
    {
        string host = EndpointHost(settings);
        if (IPAddress.TryParse(host, out var ip)) return new[] { ip };
        var addresses = await Dns.GetHostAddressesAsync(host, token);
        if (addresses.Length == 0) throw new InvalidOperationException("No VPN server addresses resolved.");
        return addresses.OrderBy(x => x.GetAddressBytes().Length).ToArray();
    }
    public static async Task WaitForAdapter(TunLease lease, CancellationToken token)
    {
        for (int i = 0; i < 200; i++)
        {
            token.ThrowIfCancellationRequested();
            if (lease.Exited) throw new InvalidOperationException("Adapter recovery guard stopped.");
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => Guid.TryParse(x.Id, out var id) && id == lease.AdapterId);
            if (nic?.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().UnicastAddresses.Any(x => x.Address.ToString() == "172.28.255.1")) return;
            await Task.Delay(100, token);
        }
        string devices = string.Join("; ", NetworkInterface.GetAllNetworkInterfaces().Where(x => x.Name.StartsWith("PersonalVPN", StringComparison.Ordinal)).Select(x => x.Name + " id=" + x.Id + " state=" + x.OperationalStatus + " addresses=" + string.Join(",", x.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString()))));
        throw new InvalidOperationException("TUN adapter did not become ready. Expected=" + lease.AdapterId + "; " + devices);
    }
}