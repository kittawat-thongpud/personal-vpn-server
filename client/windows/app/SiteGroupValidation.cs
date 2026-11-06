using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalVpn;

internal static class SiteGroupValidation
{
    private static void Check(bool value,string reason) { if(!value)throw new InvalidOperationException(reason); }
    private static void Reject(string kind,string input) { try { SiteTargets.Parse(kind,input); } catch(InvalidOperationException) { return; } throw new InvalidOperationException("Invalid target accepted: "+input); }
    public static void Run()
    {
        var ip=new TunnelException{Kind="IP",Name="Private lab",Value="2.2.2.*,10.46.136.6,2.2.2.2",Action="Bypass"};
        var web=new TunnelException{Kind="Domain",Name="Web services",Value="www.google.com, www.youtube.com",Action="Block"};
        var settings=new Settings{Sites=new TunnelPolicy{Mode="Tunnel",Entries=new(){ip,web}}};
        Check(SiteTargets.Parse("IP",ip.Value).SequenceEqual(new[]{"2.2.2.0/24","10.46.136.6/32","2.2.2.2/32"}),"Grouped IPv4 wildcard compilation lost a target.");
        Check(SiteTargets.Normalize("IP","2001:db8::9/64")=="2001:db8:0:0:0:0:0:0/64","IPv6 CIDR host bits were not masked.");
        Check(SiteTargets.Normalize("IP","10.*.*.*")=="10.0.0.0/8" && SiteTargets.Normalize("IP","010.046.136.006")=="10.46.136.6/32","Decimal octets or network wildcard changed.");
        Check(SiteTargets.Clean("Domain","Example.com\nexample.com;www.youtube.com")=="Example.com, www.youtube.com","Group deduplication changed the first human label.");
        var proxy=Policy.Compile(settings);Check(proxy[1]!["domain"]!.AsArray().Count==2 && proxy[2]!["ip"]!.AsArray().Count==3,"Proxy compiler did not emit all group members or Block-first priority.");
        var tun=TunPlan.Compile(settings,new[]{"203.0.113.10"},new[]{@"C:\VPN\cores\xray.exe"});
        Check(tun["route"]!["rules"]!.AsArray().Any(x=>x?["ip_cidr"] is JsonArray a && a.Count==3),"TUN compiler lost grouped CIDRs.");
        var round=JsonSerializer.Deserialize<TunnelException>(JsonSerializer.Serialize(ip))!;Check(round.Name==ip.Name && round.Value==ip.Value,"Named site group did not round trip.");
        var old=JsonSerializer.Deserialize<TunnelException>("""{"Kind":"IP","Value":"10.46.136.6","Action":"Bypass"}""")!;
        Check(old.Name=="" && old.DisplayName==old.Value,"Legacy single-target settings changed.");
        foreach(var invalid in new[]{"2.*.3.*","256.2.2.*","1.2.3.4/33","1.2.3","::g","1.2.3.4%eth0"})Reject("IP",invalid);
        Reject("Domain","www.google.com,1.2.3.4");Reject("Domain","https://example.com");
        var tooMany=string.Join(",",Enumerable.Range(0,501).Select(i=>"site"+i+".example"));Reject("Domain",tooMany);
        settings.Sites.Entries.Add(new(){Kind="IP",Value="2.2.2.1/24"});bool duplicate=false;
        try { Policy.ValidateTunnel(settings.Sites,false); }catch(InvalidOperationException){duplicate=true;}
        Check(duplicate,"Equivalent networks in separate groups were accepted.");
    }
}
