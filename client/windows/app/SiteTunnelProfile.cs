using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PersonalVpn;

internal static class SiteTunnelProfile
{
    private const string Format = "personal-vpn.site-tunnel";
    internal const string FileName = "personal-vpn-site-tunnel.json";
    private const int MaxBytes = 512 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Export(TunnelPolicy policy)
    {
        Policy.ValidateTunnel(policy, false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = Format,
            version = 1,
            mode = policy.Mode,
            rules = policy.Entries.Select(rule => new
            {
                name = rule.Name,
                kind = rule.Kind,
                target = SiteTargets.Clean(rule.Kind, rule.Value),
                action = rule.Action
            })
        }, Store.JsonOptions);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Site Tunnel file exceeds 512 KiB.");
        return bytes;
    }

    internal static TunnelPolicy Import(Stream stream)
    {
        byte[] bytes = new byte[MaxBytes + 1];
        int size = stream.ReadAtLeast(bytes, bytes.Length, throwIfEndOfStream: false);
        if (size > MaxBytes) throw new InvalidDataException("Site Tunnel file exceeds 512 KiB.");
        try
        {
            using var document = JsonDocument.Parse(Utf8.GetString(bytes, 0, size).TrimStart('\uFEFF'));
            var root = document.RootElement;
            RequireFields(root, "format", "version", "mode", "rules");
            var version = root.GetProperty("version");
            if (ReadString(root, "format") != Format || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != 1)
                throw new InvalidDataException("Choose a supported Personal VPN Site Tunnel JSON file.");
            var policy = new TunnelPolicy { Mode = ReadString(root, "mode") };
            var rules = root.GetProperty("rules");
            if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > SiteTargets.Limit)
                throw new InvalidDataException("The file must contain a list of up to 500 site groups.");
            foreach (var rule in rules.EnumerateArray())
            {
                RequireFields(rule, "name", "kind", "target", "action");
                policy.Entries.Add(new TunnelException
                {
                    Name = ReadString(rule, "name"), Kind = ReadString(rule, "kind"),
                    Value = ReadString(rule, "target"), Action = ReadString(rule, "action")
                });
            }
            Policy.ValidateTunnel(policy, false);
            foreach (var rule in policy.Entries) rule.Value = SiteTargets.Clean(rule.Kind, rule.Value);
            return policy;
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("The Site Tunnel file is not valid UTF-8 JSON.", error);
        }
    }

    private static string ReadString(JsonElement obj, string key)
    {
        var value = obj.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Site Tunnel field: " + key + ".");
        return value.GetString()!;
    }

    private static void RequireFields(JsonElement obj, params string[] expected)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual(expected.OrderBy(x => x, StringComparer.Ordinal)))
            throw new InvalidDataException("The file does not match the Personal VPN Site Tunnel format.");
    }
}
