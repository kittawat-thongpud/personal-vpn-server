using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;

namespace PersonalVpn;

// A configuration compiler, deliberately NOT an adapter/route launcher. The
// elevated Windows host and teardown qualification are the next implementation.
public static class TunPlan
{
    public static JsonObject Compile(Settings source, IEnumerable<string> transportAddresses, IEnumerable<string> ownedCorePaths)
    {
        var settings = source.Snapshot(); Policy.Upgrade(settings);
        Policy.ValidateTunnel(settings.Apps, true); Policy.ValidateTunnel(settings.Sites!, false);
        var exclusions = transportAddresses.Distinct().Select(value =>
        {
            if (!IPAddress.TryParse(value, out var ip)) throw new InvalidOperationException("Resolve transport addresses before creating a TUN plan.");
            return ip + (ip.GetAddressBytes().Length == 4 ? "/32" : "/128");
        }).ToArray();
        if (exclusions.Length == 0) throw new InvalidOperationException("A TUN plan requires explicit transport IP exclusions to prevent recursion.");
        var paths = ownedCorePaths.ToArray();
        Policy.ValidateTunnel(new TunnelPolicy { Entries = paths.Select(x => new TunnelException { Kind = "App", Value = x, Action = "Bypass" }).ToList() }, true);
        if (paths.Length == 0) throw new InvalidOperationException("Owned VPN core paths must be excluded from capture.");
        var rules = new JsonArray
        {
            new JsonObject { ["process_path"] = Strings(paths), ["action"] = "route", ["outbound"] = "direct" },
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" }
        };
        // Explicit app rules win even when a site has an explicit Block rule.
        // DNS/control routing precedes user data rules; defaults come last.
        if (settings.Apps.Mode != "Disabled") foreach (var entry in settings.Apps.Entries) rules.Add(Rule(entry, "process_path"));
        if (settings.Sites!.Mode != "Disabled")
            foreach (var entry in Policy.SiteEntries(settings.Sites)) rules.Add(Rule(entry, entry.Kind == "Domain" ? "domain_suffix" : "ip_cidr"));
        string fallback = settings.Apps.Mode != "Disabled" ? settings.Apps.Mode : settings.Sites.Mode != "Disabled" ? settings.Sites.Mode : "Tunnel";
        if (fallback == "Block") rules.Add(new JsonObject { ["action"] = "reject" });
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["type"] = "https", ["tag"] = "remote-dns", ["server"] = "1.1.1.1", ["path"] = "/dns-query", ["detour"] = "tunnel" }),
                ["final"] = "remote-dns", ["reverse_mapping"] = true
            },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "tun", ["tag"] = "wintun", ["interface_name"] = "PersonalVPN",
                ["address"] = new JsonArray("172.28.255.1/30", "fd26:706e:1::1/126"),
                ["mtu"] = 1400, ["auto_route"] = true, ["strict_route"] = true,
                ["route_exclude_address"] = Strings(exclusions), ["stack"] = "mixed"
            }),
            ["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["tag"] = "tunnel", ["server"] = "127.0.0.1", ["server_port"] = settings.SocksPort, ["version"] = "5" },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject { ["auto_detect_interface"] = true, ["default_domain_resolver"] = "remote-dns", ["rules"] = rules, ["final"] = fallback == "Block" ? "tunnel" : Policy.Tag(fallback) }
        };
    }
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    private static JsonObject Rule(TunnelException entry, string field)
    {
        var rule = new JsonObject { [field] = new JsonArray(entry.Value), ["action"] = entry.Action == "Block" ? "reject" : "route" };
        if (entry.Action != "Block") rule["outbound"] = Policy.Tag(entry.Action); return rule;
    }
}
