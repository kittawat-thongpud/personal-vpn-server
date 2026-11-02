using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PersonalVpn;

public sealed record ProxySnapshot(uint Flags, string Server, string Bypass, string AutoConfig);
public sealed record ProxyLeaseRecord(int OwnerPid, long OwnerStart, string Id, ProxySnapshot Before, ProxySnapshot Applied);

// WinINet per-user LAN settings only. Never changes WinHTTP, adapters, routes or DNS.
public static class WindowsProxy
{
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Option
    {
        [FieldOffset(0)] public uint Id;
        [FieldOffset(8)] public uint Flags;
        [FieldOffset(8)] public IntPtr String;
    }
    [StructLayout(LayoutKind.Sequential)] private struct OptionList
    {
        public uint Size;
        public IntPtr Connection;
        public uint Count, Error;
        public IntPtr Options;
    }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)] private static extern bool Query(IntPtr internet, uint option, ref OptionList list, ref uint length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] private static extern bool Set(IntPtr internet, uint option, ref OptionList list, uint length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] private static extern bool Notify(IntPtr internet, uint option, IntPtr data, uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    private static OptionList Allocate()
    {
        if (IntPtr.Size != 8) throw new InvalidOperationException("This Windows release requires x64.");
        return new OptionList { Size = (uint)Marshal.SizeOf<OptionList>(), Count = 4, Options = Marshal.AllocHGlobal(4 * Marshal.SizeOf<Option>()) };
    }
    public static ProxySnapshot Read()
    {
        var list = Allocate();
        try
        {
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Id = (uint)(i + 1) }, IntPtr.Add(list.Options, i * 16), false);
            Marshal.StructureToPtr(new Option { Id = 10 }, list.Options, false); // FLAGS_UI, as recommended on Windows 7+.
            uint length = list.Size;
            if (!Query(IntPtr.Zero, 75, ref list, ref length))
            {
                Marshal.StructureToPtr(new Option { Id = 1 }, list.Options, false);
                length = list.Size;
                if (!Query(IntPtr.Zero, 75, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Windows proxy settings.");
            }
            var flags = Marshal.PtrToStructure<Option>(list.Options).Flags;
            string[] strings = new string[3];
            for (int i = 1; i < 4; i++)
            {
                IntPtr pointer = Marshal.PtrToStructure<Option>(IntPtr.Add(list.Options, i * 16)).String;
                try { strings[i - 1] = pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pointer) ?? ""; }
                finally { if (pointer != IntPtr.Zero) GlobalFree(pointer); }
            }
            return new ProxySnapshot(flags, strings[0], strings[1], strings[2]);
        }
        finally { Marshal.FreeHGlobal(list.Options); }
    }
    public static void Write(ProxySnapshot value)
    {
        var list = Allocate(); var allocated = new IntPtr[3];
        try
        {
            Marshal.StructureToPtr(new Option { Id = 1, Flags = value.Flags }, list.Options, false);
            string[] values = { value.Server, value.Bypass, value.AutoConfig };
            for (int i = 0; i < 3; i++)
            {
                allocated[i] = Marshal.StringToHGlobalUni(values[i]);
                Marshal.StructureToPtr(new Option { Id = (uint)(i + 2), String = allocated[i] }, IntPtr.Add(list.Options, (i + 1) * 16), false);
            }
            if (!Set(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not apply Windows proxy settings.");
            Notify(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
            Notify(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
        }
        finally { foreach (var pointer in allocated) if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(list.Options); }
    }
    public static ProxySnapshot ForPort(int port) => new(2, "http=127.0.0.1:" + port + ";https=127.0.0.1:" + port, "localhost;127.0.0.1;[::1]", "");
    public static bool Matches(ProxySnapshot current, ProxySnapshot expected) =>
        (current.Flags & 15) == (expected.Flags & 15) && current.Server == expected.Server && current.Bypass == expected.Bypass && current.AutoConfig == expected.AutoConfig;
}

public sealed class SystemProxyLease : IDisposable
{
    private const string FileName = "system-proxy.dpapi";
    private readonly ProxyLeaseRecord record;
    private readonly SessionLog log;
    private bool disposed;
    private static string LeasePath => Path.Combine(Store.Root, FileName);
    private static Mutex Gate() => new(false, @"Local\PersonalVPN-ProxyLease-" + Environment.UserName);
    private static ProxyLeaseRecord? ReadLease() => File.Exists(LeasePath)
        ? JsonSerializer.Deserialize<ProxyLeaseRecord>(Store.Protect(File.ReadAllBytes(LeasePath), false)) : null;
    private static void SaveLease(ProxyLeaseRecord record)
    {
        Store.SecureDirectory(Store.Root);
        File.WriteAllBytes(LeasePath + ".new", Store.Protect(JsonSerializer.SerializeToUtf8Bytes(record), true));
        File.Move(LeasePath + ".new", LeasePath, true);
    }
    private static bool Alive(ProxyLeaseRecord lease)
    {
        try { using var process = Process.GetProcessById(lease.OwnerPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.OwnerStart; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    private static bool Lock(Mutex gate)
    {
        try { return gate.WaitOne(TimeSpan.FromSeconds(8)); }
        catch (AbandonedMutexException) { return true; }
    }
    public static void RecoverAbandoned()
    {
        using var gate = Gate(); if (!Lock(gate)) throw new InvalidOperationException("Proxy recovery is busy.");
        try
        {
            var lease = ReadLease();
            if (lease != null && !Alive(lease)) Restore(lease);
        }
        finally { gate.ReleaseMutex(); }
    }
    public SystemProxyLease(int port, SessionLog log)
    {
        this.log = log;
        using var gate = Gate(); if (!Lock(gate)) throw new InvalidOperationException("Windows proxy is busy.");
        try
        {
            var previous = ReadLease();
            if (previous != null)
            {
                if (Alive(previous)) throw new InvalidOperationException("Another Personal VPN session owns Windows proxy settings.");
                Restore(previous);
            }
            using var owner = Process.GetCurrentProcess();
            record = new(owner.Id, owner.StartTime.ToUniversalTime().Ticks, Guid.NewGuid().ToString("N"), WindowsProxy.Read(), WindowsProxy.ForPort(port));
            SaveLease(record); // Durable encrypted recovery data exists before mutation.
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--proxy-guard"); start.ArgumentList.Add(record.Id);
                using var guard = Process.Start(start) ?? throw new InvalidOperationException("Could not start proxy recovery watchdog.");
                WindowsProxy.Write(record.Applied);
                if (!WindowsProxy.Matches(WindowsProxy.Read(), record.Applied)) throw new InvalidOperationException("Windows did not retain the requested proxy settings.");
                log.Add("INFO", "backend.system_proxy_applied", "Windows HTTP / HTTPS proxy now uses Personal VPN on loopback port " + port + ". Previous settings saved for restore.");
            }
            catch
            {
                Restore(record); throw;
            }
        }
        finally { gate.ReleaseMutex(); }
    }
    public bool IsCurrent => WindowsProxy.Matches(WindowsProxy.Read(), record.Applied);
    private static bool Restore(ProxyLeaseRecord lease)
    {
        // Do not overwrite a newer proxy choice made by the user / another VPN.
        bool ours = WindowsProxy.Matches(WindowsProxy.Read(), lease.Applied);
        if (ours) WindowsProxy.Write(lease.Before);
        File.Delete(LeasePath);
        return ours;
    }
    public void Dispose()
    {
        if (disposed) return;
        using var gate = Gate(); if (!Lock(gate)) throw new InvalidOperationException("Proxy restore is busy; recovery data was retained.");
        try
        {
            var saved = ReadLease();
            if (saved?.Id == record.Id)
            {
                bool restored = Restore(record);
                log.Add("INFO", "backend.system_proxy_restored", restored ? "Previous Windows proxy settings restored." : "Windows proxy changed outside Personal VPN; kept the newer setting.");
            }
            disposed = true;
        }
        finally { gate.ReleaseMutex(); }
    }
    public static async Task<int> Watch(string id)
    {
        ProxyLeaseRecord? lease;
        using (var gate = Gate())
        {
            if (!Lock(gate)) return 1;
            try { lease = ReadLease(); if (lease?.Id != id) return 0; }
            finally { gate.ReleaseMutex(); }
        }
        // Poll the ownership record too: disconnect ends the watchdog while GUI remains open.
        while (Alive(lease))
        {
            await Task.Delay(1000);
            using var gate = Gate(); if (!Lock(gate)) continue;
            try { if (ReadLease()?.Id != id) return 0; }
            finally { gate.ReleaseMutex(); }
        }
        using (var gate = Gate())
        {
            if (!Lock(gate)) return 1;
            try { if (ReadLease()?.Id == id) Restore(lease); }
            finally { gate.ReleaseMutex(); }
        }
        return 0;
    }
}
