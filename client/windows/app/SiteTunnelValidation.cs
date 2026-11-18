using System;
using System.IO;
using System.Linq;
using System.Text;

namespace PersonalVpn;

internal static partial class Tests
{
    private static void SiteTunnelTransferPolicy()
    {
        using var fixture = typeof(Tests).Assembly.GetManifestResourceStream("PersonalVpn.SiteTunnelV1.json")
            ?? throw new InvalidOperationException("Shared Site Tunnel fixture is unavailable.");
        var policy = SiteTunnelProfile.Import(fixture);
        Assert(policy.Mode == "Bypass" && policy.Entries.Select(x => x.Action).SequenceEqual(new[] { "Tunnel", "Block", "Bypass" }) &&
            policy.Entries[0].Name == "รายการเว็บ", "Shared Site Tunnel profile lost mode, actions or names.");
        Assert(SiteTargets.Parse("IP", policy.Entries[1].Value).SequenceEqual(new[] { "10.46.136.6/32", "192.0.2.0/24", "2001:db8:0:0:0:0:0:0/32" }),
            "Shared profile lost wildcard, CIDR or plain IP behavior.");
        byte[] encoded = SiteTunnelProfile.Export(policy);
        using var serialized = new MemoryStream(encoded);
        Assert(SiteTunnelProfile.Export(SiteTunnelProfile.Import(serialized)).SequenceEqual(encoded), "Site Tunnel export did not round-trip.");
        var invalid = new[] { Encoding.UTF8.GetBytes("{\"profiles\":[]}"),
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(encoded).Replace("\"version\": 1", "\"version\": 2")), new byte[512 * 1024 + 1] };
        foreach (var bytes in invalid)
        {
            using var input = new MemoryStream(bytes);
            bool rejected = false;
            try { SiteTunnelProfile.Import(input); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Invalid Site Tunnel profile was accepted.");
        }
    }
}
