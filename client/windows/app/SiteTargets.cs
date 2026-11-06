using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PersonalVpn;

public static class SiteTargets
{
    public const int Limit = 500, InputLimit = 131072;
    private static string[] Tokens(string text)
    {
        if (text.Length > InputLimit) throw new InvalidOperationException("Target list is too long.");
        var values = text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (values.Length == 0) throw new InvalidOperationException("Enter at least one target.");
        return values;
    }
    public static string Clean(string kind, string text)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", Tokens(text).Where(x => unique.Add(Normalize(kind,x))));
    }
    public static string[] Parse(string kind, string text)
    {
        var values = Tokens(text).Select(x => Normalize(kind,x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length > Limit) throw new InvalidOperationException("Use at most 500 targets across site groups.");
        return values;
    }
    public static string Normalize(string kind, string value)
    {
        if (kind == "Domain")
        {
            Policy.Validate(new CategoryRule { Entries = new() { value } },true);
            return value.ToLowerInvariant();
        }
        if (kind != "IP") throw new InvalidOperationException("Choose Domain or IP.");
        if (value.Contains('*'))
        {
            var parts = value.Split('.'); int first = Array.IndexOf(parts,"*");
            if (parts.Length != 4 || first < 0 || parts.Skip(first).Any(x => x != "*") || parts.Take(first).Any(x => !Regex.IsMatch(x,@"^[0-9]{1,3}$") || !int.TryParse(x,out int n) || n > 255))
                throw new InvalidOperationException("IPv4 wildcards must be trailing octets, such as 2.2.2.* or 10.*.*.*.");
            value = string.Join(".",parts.Take(first).Select(x => int.Parse(x).ToString()).Concat(Enumerable.Repeat("0",4-first))) + "/" + first*8;
        }
        var fields = value.Split('/');
        if (!fields[0].Contains(':'))
        {
            var octets=fields[0].Split('.');
            if(octets.Length!=4 || octets.Any(x=>!Regex.IsMatch(x,@"^[0-9]{1,3}$") || !int.TryParse(x,out int n) || n>255)) throw new InvalidOperationException("Enter a complete IPv4 address with four decimal octets.");
            fields[0]=string.Join(".",octets.Select(x=>int.Parse(x).ToString()));
        }
        if (fields.Length > 2 || !IPAddress.TryParse(fields[0],out var address) || fields[0].Contains('%')) throw new InvalidOperationException("Enter an IPv4/IPv6 address, CIDR or trailing IPv4 wildcard.");
        byte[] bytes = address.GetAddressBytes(); int bits = bytes.Length*8;
        int prefix = bits;
        if (fields.Length == 2 && (!Regex.IsMatch(fields[1],@"^[0-9]{1,3}$") || !int.TryParse(fields[1],out prefix) || prefix > bits)) throw new InvalidOperationException("Invalid CIDR prefix.");
        for (int i=0;i<bytes.Length;i++) { int keep=Math.Clamp(prefix-i*8,0,8); bytes[i] &= (byte)(0xff << (8-keep)); }
        string host = bytes.Length == 4 ? string.Join(".",bytes) : string.Join(":",Enumerable.Range(0,8).Select(i => ((bytes[2*i]<<8)|bytes[2*i+1]).ToString("x")));
        return host+"/"+prefix;
    }
}
