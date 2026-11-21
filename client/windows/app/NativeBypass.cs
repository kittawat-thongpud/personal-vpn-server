using System;
using System.Collections.Generic;
using System.IO;
#if PVN_WFP_LAB
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
#endif

namespace PersonalVpn;

// The reference driver is qualified only in the disposable Windows lab. A
// build without that runtime still reads saved rules so they can be edited.
internal static class NativeBypass
{
    private const string JournalName = "native-bypass.json";
    internal static bool Available =>
#if PVN_WFP_LAB
        Environment.Is64BitProcess && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted";
#else
        false;
#endif

    internal static void EnsureAvailable(IReadOnlyCollection<string> paths)
    {
        if (paths.Count > 0 && !Available)
            throw new InvalidOperationException("Bypass VPN needs the qualified Windows native runtime. Select Direct via TUN or turn off the affected rules.");
    }

    internal static IDisposable? Start(string folder, string[] paths)
    {
        if (paths.Length == 0) return null;
        EnsureAvailable(paths);
#if PVN_WFP_LAB
        return new PreviewLease(Path.Combine(folder, JournalName), paths);
#else
        throw new PlatformNotSupportedException();
#endif
    }

    internal static void CheckLink(IDisposable? lease)
    {
#if PVN_WFP_LAB
        if (lease is PreviewLease preview) preview.CheckLink();
#endif
    }

    internal static void Recover(string folder)
    {
        string journal = Path.Combine(folder, JournalName);
        if (!File.Exists(journal)) return;
        if (!Available) throw new InvalidOperationException("Native Bypass recovery needs the Windows test runtime.");
#if PVN_WFP_LAB
        var owner = JsonSerializer.Deserialize<PreviewOwner>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Invalid native bypass journal.");
        WfpReferenceLab.Recover(owner.Baseline, owner.Dns);
        File.Delete(journal);
#endif
    }

#if PVN_WFP_LAB
    private sealed record PreviewOwner(Guid Baseline, Guid Dns);

    private static string UnderlayStamp() => string.Join("|", PhysicalNetwork.Rows().Select(pair =>
    {
        var props = pair.Nic.GetIPProperties();
        return pair.Nic.Id + ":" + pair.Value.Family + ":" + pair.Value.Metric + ":" +
            string.Join(",", props.UnicastAddresses.Select(address => address.Address.ToString()).OrderBy(value => value)) + ":" +
            string.Join(",", props.GatewayAddresses.Select(address => address.Address.ToString()).OrderBy(value => value));
    }).OrderBy(value => value));

    private static IPAddress GetUnderlaySource()
    {
        // UDP connect selects a source via the current route table without
        // sending a packet. The guard runs before Wintun has capture routes.
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443));
        var address = ((IPEndPoint)socket.LocalEndPoint!).Address;
        if (!PhysicalNetwork.Rows().Any(pair => pair.Value.Family == 2 && pair.Nic.GetIPProperties().UnicastAddresses.Any(value => value.Address.Equals(address))))
            throw new InvalidOperationException("Bypass VPN needs an active physical IPv4 route in this Windows test runtime.");
        return address;
    }

    private sealed class PreviewLease : IDisposable
    {
        private readonly string journal;
        private readonly string stamp;
        private readonly WfpReferenceLab driver;

        internal PreviewLease(string journal, string[] paths)
        {
            this.journal = journal;
            string installation = Path.GetFullPath(AppContext.BaseDirectory);
            string windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
            if (paths.Any(path => path.StartsWith(installation, StringComparison.OrdinalIgnoreCase) || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Choose a user application. VPN executables and Windows system processes use managed routes.");
            stamp = UnderlayStamp();
            var source = GetUnderlaySource();
            var owner = new PreviewOwner(Guid.NewGuid(), Guid.NewGuid());
            using (var file = new FileStream(journal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, owner);
                file.Flush(flushToDisk: true);
            }
            driver = new WfpReferenceLab(owner.Baseline, owner.Dns);
            try { driver.Configure(paths, IPAddress.Parse(TunPlan.Ipv4Address), source, IPAddress.Parse(TunPlan.Ipv6Address)); }
            catch { Dispose(); throw; }
        }

        internal void CheckLink()
        {
            if (stamp == UnderlayStamp()) return;
            driver.Clear();
            throw new InvalidOperationException("The physical link changed. Native Bypass was cleared and capture will be re-verified.");
        }

        public void Dispose()
        {
            driver.Dispose();
            File.Delete(journal);
        }
    }
#endif
}
