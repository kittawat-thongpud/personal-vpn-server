using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

internal static partial class Tests
{
    internal static void NetworkRecoveryPolicy()
    {
        Assert(Marshal.SizeOf<PhysicalNetwork.Row>() == 168 && Marshal.OffsetOf<PhysicalNetwork.Row>("Luid").ToInt32() == 8, "Physical IP interface ABI mismatch.");
        var row = new PhysicalNetwork.Row { Family = 2, Forwarding = 0, WeakHostSend = 0, WeakHostReceive = 1, Metric = 777, Mtu = 1390, AutomaticMetric = 1, DisableDefaultRoutes = 1, ManagedAddress = 1, RouterDiscovery = 2 };
        Assert(PhysicalNetworkLease.RestoreFields(new() { Forwarding = true }, ref row), "Forwarding recovery was skipped.");
        Assert(row.Forwarding == 1 && row.WeakHostSend == 0 && row.WeakHostReceive == 1 && row.Metric == 777 && row.Mtu == 1390 && row.AutomaticMetric == 1 && row.DisableDefaultRoutes == 1 && row.ManagedAddress == 1 && row.RouterDiscovery == 2, "Field recovery overwrote unrelated properties.");
        Assert(!PhysicalNetworkLease.RestoreFields(new() { Forwarding = true }, ref row), "Recovery was not idempotent.");
        row.WeakHostSend = 1;
        Assert(!PhysicalNetworkLease.RestoreFields(new() { Forwarding = true }, ref row) && row.WeakHostSend == 1, "Recovery overwrote another manager's weak-host change.");
        var journal = new PhysicalNetworkLease.Entry { Id = Guid.NewGuid(), Family = 23, WeakHostSend = true };
        var copy = JsonSerializer.Deserialize<PhysicalNetworkLease.Entry>(JsonSerializer.Serialize(journal))!;
        Assert(copy.Id == journal.Id && copy.Family == 23 && copy.WeakHostSend && !copy.Forwarding, "Physical recovery journal lost ownership fields.");
    }
    private static string PhysicalSnapshot() => string.Join("|", PhysicalNetwork.Rows(false).Select(x =>
    {
        var r = x.Value;
        var ip = x.Nic.GetIPProperties();
        return x.Nic.Id + ":" + r.Family + ":" + r.Forwarding + ":" + r.WeakHostSend + ":" + r.WeakHostReceive + ":" + r.AutomaticMetric + ":" + r.Metric + ":" + r.Mtu + ":" + r.DisableDefaultRoutes + ":" + r.RouterDiscovery + ":" + r.ManagedAddress + ":" + r.OtherStateful + ":" + r.NeighborDetection + ":" + (r.Family == 2 ? ip.GetIPv4Properties().IsDhcpEnabled.ToString() : "IPv6") + ":" + string.Join(",",ip.DnsAddresses);
    }).OrderBy(x => x));
    // Deliberate fixture writes are isolated CI only. Never enable forwarding
    // in a workstation self-test or in the operator's host diagnostic.
    internal static async Task<int> NetworkRecoveryIntegration(string[] args)
    {
        var checks = new List<string>();
        string folder = Path.Combine(Store.Root, "runtime", Guid.NewGuid().ToString("N"));
        PhysicalNetwork.Row? original = null; Guid id = Guid.Empty;
        try
        {
            Assert(Environment.GetEnvironmentVariable("CI") == "true", "Network mutation fixture runs only on an isolated CI runner.");
            var target = PhysicalNetwork.Rows().First(x => x.Value.Family == 2 && x.Value.Connected != 0);
            id = Guid.Parse(target.Nic.Id); original = target.Value;
            var fixture = target.Value; fixture.Forwarding = 1; fixture.WeakHostSend = 1; PhysicalNetwork.Write(ref fixture);
            string before = PhysicalSnapshot();
            Store.SecureDirectory(folder);
            var lease = new PhysicalNetworkLease(folder); lease.Prepare();
            var current = PhysicalNetwork.Read(id, 2)!.Value;
            Assert(current.Forwarding == 0 && current.WeakHostSend == 0, "Strong-host preparation failed.");
            Assert(File.Exists(Path.Combine(folder, "physical-network.json")), "Physical mutation has no durable recovery journal.");
            lease.Restore(); Assert(before == PhysicalSnapshot(), "Normal restore changed physical settings.");
            checks.Add("forwarding/weak-host lease and stable physical property restore");
            // Re-read disk recovery data as a different process would.
            lease = new PhysicalNetworkLease(folder); lease.Prepare();
            new PhysicalNetworkLease(folder).Restore();
            Assert(before == PhysicalSnapshot(), "Restarted journal recovery changed stable physical properties.");
            checks.Add("journal survives lease object loss; DHCP/MTU/metric/weak-receive properties preserved");
            // Exercise the independent guard, then force its GUI owner to die.
            AdapterService.EnsureRunning();
            string ready = Path.Combine(folder, "ready.json");
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var value in new[] { "--tun-lease-child", "--folder", folder, "--report", ready }) start.ArgumentList.Add(value);
            using var child = Process.Start(start)!;
            try
            {
                for (int i = 0; i < 500 && !File.Exists(ready); i++) { Assert(!child.HasExited, "Network guard child failed before readiness."); await Task.Delay(100); }
                Assert(File.Exists(ready), "Network guard fixture timed out.");
                Assert(PhysicalNetwork.Read(id,2)!.Value.Forwarding == 0, "Guard did not lease the physical interface.");
                child.Kill(); await child.WaitForExitAsync();
                for (int i = 0; i < 500 && !File.Exists(Path.Combine(folder,"tun-reset-done")); i++) await Task.Delay(100);
                Assert(File.Exists(Path.Combine(folder,"tun-reset-done")) && before == PhysicalSnapshot(), "Abrupt owner death did not restore physical flags and capture.");
            }
            finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
            checks.Add("forced GUI/native death: guard restores physical flags and releases TUN routes/DNS");
            return Report(args,new { result="PASS", checks },0);
        }
        catch(Exception error) { return Report(args,new { result="FAIL", checks, reason=error.Message },1); }
        finally
        {
            if (id != Guid.Empty && original != null)
            {
                TunLease.RecoverAbandoned();
                var live = PhysicalNetwork.Read(id,2);
                if (live != null) { var r=live.Value; r.Forwarding=original.Value.Forwarding; r.WeakHostSend=original.Value.WeakHostSend; PhysicalNetwork.Write(ref r); }
            }
            if (Directory.Exists(folder)) TunLease.CleanRuntimeFolder(folder);
        }
    }
}
