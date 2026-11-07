using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PersonalVpn;

// Only these two fields need a strong-host lease for Winsock interface binding.
// Never initialize a fresh row for SetIpInterfaceEntry: that resets other fields.
internal static class PhysicalNetwork
{
    [StructLayout(LayoutKind.Explicit, Size = 168)]
    internal struct Row
    {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(8)] public ulong Luid;
        [FieldOffset(16)] public uint Index;
        [FieldOffset(41)] public byte Forwarding;
        [FieldOffset(42)] public byte WeakHostSend;
        [FieldOffset(43)] public byte WeakHostReceive;
        [FieldOffset(44)] public byte AutomaticMetric;
        [FieldOffset(45)] public byte NeighborDetection;
        [FieldOffset(46)] public byte ManagedAddress;
        [FieldOffset(47)] public byte OtherStateful;
        [FieldOffset(52)] public uint RouterDiscovery;
        [FieldOffset(144)] public uint SitePrefixLength;
        [FieldOffset(148)] public uint Metric;
        [FieldOffset(152)] public uint Mtu;
        [FieldOffset(156)] public byte Connected;
        [FieldOffset(166)] public byte DisableDefaultRoutes;
    }
    internal static bool IsUnderlay(NetworkInterface nic) => nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        !new[] { "PersonalVPN", "ZeroTier", "Tailscale", "VPN", "TAP", "TUN" }.Any(x => (nic.Name + " " + nic.Description).Contains(x, StringComparison.OrdinalIgnoreCase));
    internal static IEnumerable<(NetworkInterface Nic, Row Value)> Rows(bool upOnly = true)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(x => IsUnderlay(x) && (!upOnly || x.OperationalStatus == OperationalStatus.Up)))
        {
            if (!Guid.TryParse(nic.Id, out var id)) continue;
            foreach (ushort family in new ushort[] { 2, 23 })
            {
                var row = Read(id, family);
                if (row != null) yield return (nic, row.Value);
            }
        }
    }
    internal static Row? Read(Guid id, ushort family)
    {
        if (id == Guid.Empty || family is not (2 or 23)) throw new ArgumentException("Invalid physical interface identity / family.");
        uint code = ConvertInterfaceGuidToLuid(ref id, out ulong luid);
        if (code is 2 or 1168) return null;
        // ConvertInterfaceGuidToLuid reports INVALID_PARAMETER for a GUID that
        // disappeared (e.g. unplugged USB Ethernet), not only NOT_FOUND.
        if (code == 87 && !NetworkInterface.GetAllNetworkInterfaces().Any(x => Guid.TryParse(x.Id,out var present) && present == id)) return null;
        if (code != 0) throw new Win32Exception((int)code, "Read physical interface identity failed.");
        var row = new Row { Family = family, Luid = luid };
        code = GetIpInterfaceEntry(ref row);
        if (code is 2 or 1168) return null;
        if (code != 0) throw new Win32Exception((int)code, "Read physical interface properties failed.");
        return row;
    }
    internal static void Write(ref Row row)
    {
        if (row.Family == 2) row.SitePrefixLength = 0; // Required by SetIpInterfaceEntry for IPv4.
        uint code = SetIpInterfaceEntry(ref row);
        if (code != 0) throw new Win32Exception((int)code, "Update physical interface binding flags failed.");
    }
    // Managed lease fields are deliberately omitted: our own changes must not
    // restart the session. Native route notifications handle destination routes.
    internal static string Signature() => string.Join(";", Rows().Select(x => x.Nic.Id + ":" + x.Value.Family + ":" + x.Value.Metric + ":" + x.Value.Mtu + ":" + x.Value.DisableDefaultRoutes).OrderBy(x => x));
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceGuidToLuid(ref Guid guid, out ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint GetIpInterfaceEntry(ref Row row);
    [DllImport("iphlpapi.dll")] private static extern uint SetIpInterfaceEntry(ref Row row);
}

// The independent TUN guard owns this journal, including hot-plugged links.
// Save + Flush(true) happens BEFORE mutation. Restore is GUID/family/field scoped
// and read-modify-write, preserving DHCP, DNS, MTU, metrics and outside changes.
internal sealed class PhysicalNetworkLease
{
    internal sealed class Entry
    {
        public Entry() { }
        public Guid Id { get; set; }
        public ushort Family { get; set; }
        public bool Forwarding { get; set; }
        public bool WeakHostSend { get; set; }
    }
    private readonly string path;
    private readonly List<Entry> entries;
    internal PhysicalNetworkLease(string folder)
    {
        path = Path.Combine(folder, "physical-network.json");
        entries = File.Exists(path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path))! : new();
        if (entries.Count > 256 || entries.Any(x => x.Id == Guid.Empty || x.Family is not (2 or 23))) throw new InvalidDataException("Invalid physical interface recovery record.");
    }
    private void Save()
    {
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, entries); file.Flush(true); }
        File.Move(path + ".new", path, true);
    }
    internal void Prepare()
    {
        foreach (var (nic, original) in PhysicalNetwork.Rows())
        {
            Guid id = Guid.Parse(nic.Id);
            var previous = entries.SingleOrDefault(x => x.Id == id && x.Family == original.Family);
            if (previous != null)
            {
                // Do not fight a sharing service / another network manager.
                if (original.Forwarding != 0 || original.WeakHostSend != 0) throw new InvalidOperationException("A physical interface binding flag changed outside Personal VPN. Capture stopped; previous values will be restored.");
                continue;
            }
            if (original.Forwarding == 0 && original.WeakHostSend == 0) continue;
            if (original.Forwarding != 0 && SharingActive()) throw new InvalidOperationException("Internet sharing / routing is active on a physical interface. Stop sharing before TUN; Personal VPN did not change it.");
            var entry = new Entry { Id = id, Family = original.Family, Forwarding = original.Forwarding != 0, WeakHostSend = original.WeakHostSend != 0 };
            entries.Add(entry); Save();
            var current = PhysicalNetwork.Read(id, original.Family) ?? throw new InvalidOperationException("Physical interface disappeared during preparation.");
            // Optimistic check avoids overwriting a concurrent manager change.
            if (current.Forwarding != original.Forwarding || current.WeakHostSend != original.WeakHostSend) throw new InvalidOperationException("Physical interface changed during preparation; reconnect after the network settles.");
            current.Forwarding = 0; current.WeakHostSend = 0; PhysicalNetwork.Write(ref current);
            var applied = PhysicalNetwork.Read(id, original.Family);
            if (applied == null || applied.Value.Forwarding != 0 || applied.Value.WeakHostSend != 0) throw new InvalidOperationException("Windows did not retain safe interface binding flags.");
        }
    }
    internal void Restore()
    {
        var failures = new List<Exception>();
        foreach (var entry in entries.ToArray())
        {
            try
            {
                var observed = PhysicalNetwork.Read(entry.Id, entry.Family);
                // Keep the recovery record for a removed USB NIC. Never discard
                // it simply because a device is currently absent.
                if (observed == null) continue;
                var row = observed.Value;
                bool changed = RestoreFields(entry, ref row);
                if (changed) PhysicalNetwork.Write(ref row);
                var verified = PhysicalNetwork.Read(entry.Id, entry.Family);
                if (verified == null || (entry.Forwarding && verified.Value.Forwarding == 0) || (entry.WeakHostSend && verified.Value.WeakHostSend == 0)) throw new InvalidOperationException("Physical interface recovery did not verify.");
                entries.Remove(entry); Save();
            }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException("Physical interface recovery is pending; the recovery journal was retained.", failures);
        if (entries.Count == 0 && File.Exists(path)) File.Delete(path);
    }
    internal static bool RestoreFields(Entry entry, ref PhysicalNetwork.Row row)
    {
        bool changed = false;
        if (entry.Forwarding && row.Forwarding == 0) { row.Forwarding = 1; changed = true; }
        if (entry.WeakHostSend && row.WeakHostSend == 0) { row.WeakHostSend = 1; changed = true; }
        return changed;
    }
    private static bool SharingActive()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        try
        {
            foreach (string name in new[] { "SharedAccess", "icssvc", "RemoteAccess" })
            {
                IntPtr service = OpenService(manager, name, 4);
                if (service == IntPtr.Zero) { if (Marshal.GetLastWin32Error() == 1060) continue; throw new Win32Exception(); }
                try { if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(); if (status.State != 1) return true; }
                finally { CloseServiceHandle(service); }
            }
            return false;
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Exit, SpecificExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
