using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PersonalVpn;

public sealed class ServerProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My server";
    public string Country { get; set; } = "JP";
    public string? Xray { get; set; }
    public string? Hysteria { get; set; }
    [JsonIgnore] public string Endpoint => Hysteria != null
        ? JsonNode.Parse(Hysteria)!["server"]!.GetValue<string>()
        : JsonNode.Parse(Xray!)!["outbounds"]![0]!["settings"]!["vnext"]![0]!["address"]!.GetValue<string>();
}

public sealed class CategoryRule
{
    public string Mode { get; set; } = "Disabled";
    public List<string> Entries { get; set; } = new();
}

public sealed class TunnelException
{
    public string Kind { get; set; } = "Domain";
    public string Value { get; set; } = "";
    public string Action { get; set; } = "Tunnel";
}

public sealed class TunnelPolicy
{
    public string Mode { get; set; } = "Disabled";
    public List<TunnelException> Entries { get; set; } = new();
}

public sealed class Settings
{
    public List<ServerProfile> Profiles { get; set; } = new();
    public string? SelectedId { get; set; }
    public string Protocol { get; set; } = "Hysteria 2";
    public string Traffic { get; set; } = "Global";
    public string CaptureMode { get; set; } = "Proxy";
    public bool ChainReality { get; set; }
    public bool SystemProxy { get; set; } = true;
    public int UpstreamPort { get; set; } = 7688;
    public int HttpPort { get; set; } = 17890;
    public int SocksPort { get; set; } = 17891;
    public CategoryRule Websites { get; set; } = new();
    public CategoryRule Networks { get; set; } = new();
    public TunnelPolicy? Sites { get; set; }
    public TunnelPolicy Apps { get; set; } = new();
    public Settings Snapshot() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this))!;
    [JsonIgnore] public ServerProfile Selected => Profiles.FirstOrDefault(x => x.Id == SelectedId)
        ?? throw new InvalidOperationException("Import and select a server profile first.");
    public void Validate()
    {
        _ = Selected;
        CaptureCapabilities.EnsureAvailable(CaptureMode);
        if (CaptureMode == "TUN" && ChainReality) throw new InvalidOperationException("Disable the existing upstream proxy before connecting TUN to avoid recursive capture.");
        if (HttpPort is < 1024 or > 65535 || SocksPort is < 1024 or > 65535 || HttpPort == SocksPort)
            throw new InvalidOperationException("Choose two different local ports between 1024 and 65535.");
        if (Protocol != "Hysteria 2" && Protocol != "REALITY") throw new InvalidOperationException("Select a supported protocol.");
        if (Protocol == "Hysteria 2" && Selected.Hysteria == null) throw new InvalidOperationException("This profile has no Hysteria credentials.");
        if (Protocol == "REALITY" && Selected.Xray == null) throw new InvalidOperationException("This profile has no REALITY credentials.");
        if (ChainReality && (UpstreamPort is < 1 or > 65535 || UpstreamPort == HttpPort || UpstreamPort == SocksPort))
            throw new InvalidOperationException("The upstream proxy port must be different from our listener ports.");
        Policy.Validate(Websites, true);
        Policy.Validate(Networks, false);
        if (Sites != null) Policy.ValidateTunnel(Sites, false);
        Policy.ValidateTunnel(Apps, true);
    }
}

public static class Policy
{
    public static readonly string[] Modes = { "Disabled", "Tunnel", "Bypass", "Block" };
    public static void Validate(CategoryRule rule, bool domains)
    {
        if (!Modes.Contains(rule.Mode)) throw new InvalidOperationException("Invalid rule mode.");
        if (rule.Entries.Count > 500) throw new InvalidOperationException("Limit: 500 entries per category.");
        foreach (string entry in rule.Entries)
        {
            if (domains)
            {
                if (entry.Length > 253 || !Regex.IsMatch(entry, @"^(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?$") ||
                    entry.Split('.').Any(label => label.Length == 0 || label.Length > 63 || label.StartsWith('-') || label.EndsWith('-')) || IPAddress.TryParse(entry, out _))
                    throw new InvalidOperationException("Web rules need a domain only, such as example.com. IPs belong in IP / Networks.");
            }
            else
            {
                var parts = entry.Split('/');
                if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) ||
                    (parts.Length == 2 && (!int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > ip.GetAddressBytes().Length * 8)))
                    throw new InvalidOperationException("IP rules need an IPv4/IPv6 address or CIDR.");
            }
        }
    }
    public static JsonArray Compile(Settings settings)
    {
        var rules = new JsonArray();
        // Health checks use a private inbound that always tunnels, independent of user rules.
        rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("health"), ["outboundTag"] = "tunnel" });
        if (settings.Sites != null)
        {
            var site = settings.Sites; ValidateTunnel(site, false);
            if (site.Mode != "Disabled")
                foreach (var entry in SiteEntries(site))
                    rules.Add(new JsonObject { ["type"] = "field", [entry.Kind == "Domain" ? "domain" : "ip"] = new JsonArray(entry.Kind == "Domain" ? "domain:" + entry.Value.ToLowerInvariant() : entry.Value), ["outboundTag"] = Tag(entry.Action) });
            rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = Tag(site.Mode == "Disabled" ? "Tunnel" : site.Mode) });
            return rules;
        }
        var categories = new[] { (settings.Websites, "domain"), (settings.Networks, "ip") };
        foreach (var (category, field) in categories.Where(x => x.Item1.Mode == "Block").Concat(categories.Where(x => x.Item1.Mode != "Block")))
        {
            if (category.Mode == "Disabled" || category.Entries.Count == 0) continue;
            string tag = category.Mode switch { "Tunnel" => "tunnel", "Bypass" => "direct", _ => "block" };
            rules.Add(new JsonObject { ["type"] = "field", [field] = new JsonArray(category.Entries.Select(x => JsonValue.Create(field == "domain" ? "domain:" + x.ToLowerInvariant() : x)).ToArray()), ["outboundTag"] = tag });
        }
        rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = settings.Traffic == "Global" ? "tunnel" : "direct" });
        return rules;
    }
    public static string Tag(string action) => action switch { "Tunnel" => "tunnel", "Bypass" => "direct", "Block" => "block", _ => throw new InvalidOperationException("Invalid tunnel action.") };
    public static IEnumerable<TunnelException> SiteEntries(TunnelPolicy policy) => policy.Entries.Where(x => x.Action == "Block").Concat(policy.Entries.Where(x => x.Action != "Block"));
    public static void ValidateTunnel(TunnelPolicy policy, bool apps)
    {
        if (!Modes.Contains(policy.Mode) || policy.Entries.Count > 500) throw new InvalidOperationException("Invalid policy or more than 500 exceptions.");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in policy.Entries)
        {
            _ = Tag(entry.Action);
            if (!unique.Add(entry.Kind + ":" + entry.Value)) throw new InvalidOperationException("An exception for this target already exists.");
            if (apps)
            {
                if (entry.Kind != "App" || entry.Value.Length > 1024 || !Path.IsPathFullyQualified(entry.Value) || !entry.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Choose the full path to an app.exe file.");
            }
            else
            {
                if (entry.Kind is not ("Domain" or "IP")) throw new InvalidOperationException("Choose Domain or IP / CIDR.");
                Validate(new CategoryRule { Entries = new() { entry.Value } }, entry.Kind == "Domain");
            }
        }
    }
    public static void Upgrade(Settings settings)
    {
        if (settings.Sites != null) return;
        settings.Sites = new TunnelPolicy { Mode = settings.Traffic == "Global" ? "Tunnel" : "Bypass" };
        foreach (var (category, kind) in new[] { (settings.Websites, "Domain"), (settings.Networks, "IP") })
            if (category.Mode != "Disabled")
                foreach (string value in category.Entries)
                    settings.Sites.Entries.Add(new TunnelException { Kind = kind, Value = value, Action = category.Mode });
        ValidateTunnel(settings.Sites, false);
    }
}

public static class Store
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PersonalVPN");
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static void SecureDirectory(string folder)
    {
        Directory.CreateDirectory(folder);
        var sid = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { sid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(acl);
    }
    public static Settings Load()
    {
        SecureDirectory(Root);
        string path = Path.Combine(Root, "settings.dpapi");
        if (!File.Exists(path)) return new Settings();
        return JsonSerializer.Deserialize<Settings>(Protect(File.ReadAllBytes(path), false)) ?? throw new InvalidDataException("Settings could not be decoded.");
    }
    public static void Save(Settings settings)
    {
        SecureDirectory(Root);
        string path = Path.Combine(Root, "settings.dpapi"), pending = path + ".new";
        File.WriteAllBytes(pending, Protect(JsonSerializer.SerializeToUtf8Bytes(settings), true));
        File.Move(pending, path, true);
    }
    public static ServerProfile Import(string folder)
    {
        var profile = new ServerProfile();
        foreach (string name in new[] { "xray.json", "hysteria.json" })
        {
            string path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Profile exceeds 64 KiB.");
            string json = File.ReadAllText(path);
            var doc = JsonNode.Parse(json) ?? throw new InvalidDataException("Invalid JSON profile.");
            if (name == "xray.json")
            {
                var outbound = doc["outbounds"]?.AsArray().FirstOrDefault(x => (string?)x?["protocol"] == "vless")?.DeepClone()
                    ?? throw new InvalidDataException("Expected a VLESS export.");
                if ((string?)outbound["streamSettings"]?["security"] != "reality" || !new[] { "tcp", "raw" }.Contains((string?)outbound["streamSettings"]?["network"]))
                    throw new InvalidDataException("Only TCP VLESS / REALITY exports are supported.");
                _ = Guid.Parse(outbound["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.GetValue<string>());
                // Import connection material only; do not accept routing, file logging, arbitrary dialers or listeners.
                outbound["streamSettings"]!.AsObject().Remove("sockopt");
                profile.Xray = new JsonObject { ["outbounds"] = new JsonArray(outbound) }.ToJsonString();
            }
            else
            {
                string pin = (string?)doc["tls"]?["pinSHA256"] ?? "";
                if (!Regex.IsMatch(pin.Replace(":", ""), "^[a-fA-F0-9]{64}$") || string.IsNullOrWhiteSpace((string?)doc["auth"]) || string.IsNullOrWhiteSpace((string?)doc["server"]))
                    throw new InvalidDataException("Hysteria needs server, credentials and a SHA-256 certificate pin.");
                var safe = new JsonObject();
                foreach (string field in new[] { "server", "auth", "obfs" }) if (doc[field] != null) safe[field] = doc[field]!.DeepClone();
                safe["tls"] = new JsonObject { ["sni"] = (string?)doc["tls"]?["sni"] ?? "", ["insecure"] = true, ["pinSHA256"] = pin };
                profile.Hysteria = safe.ToJsonString();
            }
        }
        if (profile.Xray == null && profile.Hysteria == null) throw new InvalidDataException("Select the folder containing the exported xray.json / hysteria.json.");
        profile.Name = "Japan · " + profile.Endpoint;
        return profile;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static byte[] Protect(byte[] data, bool encrypt)
    {
        var input = new Blob { Length = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Blob output;
            bool ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new InvalidOperationException("Windows could not protect / unlock the settings for this user.");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); Array.Clear(data); }
    }
}

public sealed record LogEntry(DateTime Time, string Level, string Event, string Message);
public sealed class SessionLog
{
    private readonly object gate = new();
    private readonly Queue<(LogEntry Entry, int Bytes)> entries = new();
    private int bytes;
    public void Add(string level, string kind, string message)
    {
        if (!(kind.StartsWith("backend.") || kind.StartsWith("state."))) return;
        var entry = new LogEntry(DateTime.Now, level, kind, message[..Math.Min(message.Length, 512)]);
        int size = JsonSerializer.SerializeToUtf8Bytes(entry).Length;
        lock (gate)
        {
            entries.Enqueue((entry, size)); bytes += size;
            while (entries.Count > 1000 || bytes > 1048576) bytes -= entries.Dequeue().Bytes;
        }
    }
    public LogEntry[] Snapshot() { lock (gate) return entries.Select(x => x.Entry).ToArray(); }
    public int Bytes { get { lock (gate) return bytes; } }
    public void Clear() { lock (gate) { entries.Clear(); bytes = 0; } }
}
