#if PVN_WFP_LAB
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PersonalVpn;

// Lab-only adapter for Mullvad's documented reference driver ABI at
// win-split-tunnel@0a0eb97f. The release project does not build this type.
internal sealed class WfpReferenceLab : IDisposable
{
    private readonly SafeFileHandle device;
    private IntPtr engine;
    private Guid baseline, dns;
    private bool initialized;

    internal WfpReferenceLab()
    {
        device = CreateFile(@"\\.\MULLVADSPLITTUNNEL", 0xc0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid) throw LastError("Open reference driver");
        try
        {
            if (State != 1) throw new InvalidOperationException("Reference driver already has policy.");
            // The driver registers filters from its own WFP session. Its filters
            // cannot reference a sublayer owned by another dynamic session.
            // These nonpersistent sublayers are deleted after the driver resets.
            var session = new Session();
            Check(FwpmEngineOpen(null, 10, IntPtr.Zero, ref session, out engine), "Open lab WFP session");
            baseline = AddSublayer(0x4000);
            dns = AddSublayer(0x4001);
            initialized = true;
            Control(1, baseline.ToByteArray().Concat(dns.ToByteArray()).ToArray());
            if (State != 2) throw new InvalidOperationException("Driver did not initialize.");
            Control(3, Pack(ProcessSnapshot(), discovery: true));
            if (State != 3) throw new InvalidOperationException("Driver did not accept process snapshot.");
        }
        catch { Dispose(); throw; }
    }

    internal ulong State => BitConverter.ToUInt64(Control(9, size: 8, minimum: 8));
    internal void Configure(string executable, IPAddress tunnel, IPAddress physical)
    {
        var ips = new byte[40];
        tunnel.GetAddressBytes().CopyTo(ips, 0);
        physical.GetAddressBytes().CopyTo(ips, 4);
        Control(4, ips); // IPv4 only; no IPv6 support is claimed by this fixture.
        Control(6, Pack(new[] { new Row(0, 0, DevicePath(executable)) }, discovery: false));
        if (State != 4) throw new InvalidOperationException("Driver did not engage.");
    }

    internal bool IsExcluded(int pid) => Control(10, BitConverter.GetBytes((ulong)pid), size: 65556, minimum: 20)[16] != 0;
    internal void Clear()
    {
        Control(8, method: 3);
        if (State != 3) throw new InvalidOperationException("Driver did not clear its app policy.");
    }

    private byte[] Control(uint function, byte[]? input = null, int size = 0, int minimum = 0, uint method = 0)
    {
        var output = new byte[size];
        if (!DeviceIoControl(device, 0x80000000u | (function << 2) | method, input, (uint)(input?.Length ?? 0), output, (uint)size, out var written, IntPtr.Zero))
            throw LastError("Reference driver operation " + function);
        if (written < minimum || written > size) throw new InvalidOperationException("Unrecognized reference ABI response.");
        return output;
    }

    private Guid AddSublayer(ushort weight)
    {
        var sublayer = new Sublayer { Key = Guid.NewGuid(), Display = new Display { Name = "Personal VPN isolated WFP fixture" }, Weight = weight };
        Check(FwpmSubLayerAdd(engine, ref sublayer, IntPtr.Zero), "Add lab WFP sublayer");
        return sublayer.Key;
    }

    private sealed record Row(uint Pid, uint Parent, string Image, long Creation = 0);
    private static byte[] Pack(IEnumerable<Row> source, bool discovery)
    {
        var rows = source.ToArray();
        var paths = rows.Select(r => Encoding.Unicode.GetBytes(r.Image)).ToArray();
        using var memory = new MemoryStream(); using var output = new BinaryWriter(memory);
        output.Write((ulong)rows.Length);
        output.Write(16UL + (ulong)rows.Length * (discovery ? 32UL : 16UL) + (ulong)paths.Sum(p => p.Length));
        ulong offset = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (discovery) { output.Write((ulong)rows[i].Pid); output.Write((ulong)rows[i].Parent); }
            output.Write(offset); output.Write(checked((ushort)paths[i].Length)); output.Write(new byte[6]);
            offset += (ulong)paths[i].Length;
        }
        foreach (var path in paths) output.Write(path);
        return memory.ToArray();
    }

    private static IEnumerable<Row> ProcessSnapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw LastError("Create process snapshot");
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (!Process32First(snapshot, ref entry)) throw LastError("Read process snapshot");
        var found = new Dictionary<uint, Row>();
        do
        {
            using var process = OpenProcess(0x1000, false, entry.Pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process.IsInvalid) continue;
            var name = new StringBuilder(32768); uint size = (uint)name.Capacity;
            string image = QueryFullProcessImageName(process, 1, name, ref size) ? name.ToString() : "";
            _ = GetProcessTimes(process, out long created, out _, out _, out _);
            found[entry.Pid] = new Row(entry.Pid, entry.Parent, image, created);
        } while (Process32Next(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw LastError("Finish process snapshot"); // ERROR_NO_MORE_FILES
        return found.Values.Select(r => found.TryGetValue(r.Parent, out var parent) && parent.Creation > 0 && parent.Creation < r.Creation ? r : r with { Parent = 0 }).ToArray();
    }

    private static string DevicePath(string path)
    {
        path = Path.GetFullPath(path);
        string root = Path.GetPathRoot(path) ?? throw new InvalidOperationException("Lab probe requires a drive path.");
        var devicePath = new StringBuilder(32768);
        if (root.Length != 3 || QueryDosDevice(root[..2], devicePath, devicePath.Capacity) == 0) throw LastError("Resolve lab probe device path");
        return devicePath.ToString().Split('\0')[0] + path[2..];
    }

    public void Dispose()
    {
        try
        {
            if (initialized)
            {
                initialized = false;
                Control(11, method: 3);
                if (State != 1) throw new InvalidOperationException("Reference driver did not reset before unload.");
            }
        }
        finally
        {
            device.Dispose();
            if (engine != IntPtr.Zero)
            {
                var handle = engine;
                engine = IntPtr.Zero;
                uint dnsStatus = dns == Guid.Empty ? 0 : FwpmSubLayerDeleteByKey(handle, ref dns);
                uint baselineStatus = baseline == Guid.Empty ? 0 : FwpmSubLayerDeleteByKey(handle, ref baseline);
                uint closeStatus = FwpmEngineClose(handle);
                Check(dnsStatus, "Remove lab WFP DNS sublayer");
                Check(baselineStatus, "Remove lab WFP baseline sublayer");
                Check(closeStatus, "Close lab WFP session");
            }
        }
    }

    private static Win32Exception LastError(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static void Check(uint error, string operation) { if (error != 0) throw new Win32Exception(unchecked((int)error), $"{operation}: 0x{error:X8}"); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Display { public string? Name, Description; }
    [StructLayout(LayoutKind.Sequential)] private struct Session { public Guid Key; public Display Display; public uint Flags, Timeout, Pid; public IntPtr Sid, Username; public int KernelMode; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct Sublayer { public Guid Key; public Display Display; public uint Flags; public IntPtr Provider; public Blob ProviderData; public ushort Weight; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ProcessEntry
    {
        public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineOpen0", CharSet = CharSet.Unicode)] private static extern uint FwpmEngineOpen(string? server, uint authentication, IntPtr identity, ref Session session, out IntPtr handle);
    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineClose0")] private static extern uint FwpmEngineClose(IntPtr handle);
    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmSubLayerAdd0")] private static extern uint FwpmSubLayerAdd(IntPtr handle, ref Sublayer sublayer, IntPtr descriptor);
    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmSubLayerDeleteByKey0")] private static extern uint FwpmSubLayerDeleteByKey(IntPtr handle, ref Guid key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, uint inputLength, [Out] byte[] output, uint outputLength, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeFileHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint QueryDosDevice(string name, StringBuilder target, int size);
}
#endif
