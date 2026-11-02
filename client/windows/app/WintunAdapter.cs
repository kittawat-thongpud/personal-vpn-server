using System;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PersonalVpn;

// Native adapter ownership for signed-driver preparation and recovery tests.
// The TUN core is the sole runtime creator; a guard recovers only its exact device.
public sealed class WintunAdapter : IDisposable
{
    private IntPtr library, adapter;
    private readonly Close close;
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr Create(string name, string tunnelType, ref Guid id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Close(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Version();
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public WintunAdapter(string name, Guid id)
    {
        if (!IsAdministrator) throw new InvalidOperationException("Run Personal VPN as Administrator.");
        library = NativeLibrary.Load(CoreIntegrity.Verify("wintun.dll"));
        close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "WintunCloseAdapter"));
        var create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "WintunCreateAdapter"));
        adapter = create(name, "PersonalVPN", ref id);
        if (adapter == IntPtr.Zero) { int code = Marshal.GetLastWin32Error(); NativeLibrary.Free(library); library = IntPtr.Zero; throw new Win32Exception(code, "Could not create Personal VPN adapter."); }
    }
    public Guid AdapterId
    {
        get
        {
            ulong luid = 0; Marshal.GetDelegateForFunctionPointer<GetLuid>(NativeLibrary.GetExport(library, "WintunGetAdapterLUID"))(adapter, ref luid);
            uint result = ConvertInterfaceLuidToGuid(ref luid, out var id); if (result != 0) throw new Win32Exception((int)result); return id;
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void GetLuid(IntPtr adapter, ref ulong luid);
    [DllImport("iphlpapi.dll")] private static extern uint ConvertInterfaceLuidToGuid(ref ulong luid, out Guid guid);
    public uint DriverVersion => Marshal.GetDelegateForFunctionPointer<Version>(NativeLibrary.GetExport(library, "WintunGetRunningDriverVersion"))();
    public void Dispose()
    {
        if (adapter != IntPtr.Zero) { close(adapter); adapter = IntPtr.Zero; }
        if (library != IntPtr.Zero) { NativeLibrary.Free(library); library = IntPtr.Zero; }
    }
    public static uint InstallDriver()
    {
        using var probe = new WintunAdapter("PersonalVPN-Install-" + Guid.NewGuid().ToString("N")[..8], Guid.NewGuid());
        return probe.DriverVersion; // No addresses, DNS or routes are assigned.
    }
    public static bool Exists(Guid id) => NetworkInterface.GetAllNetworkInterfaces().Any(x => Guid.TryParse(x.Id, out var current) && current == id);
    public static void RemoveOrphan(Guid id, string name)
    {
        if (!name.StartsWith("PersonalVPN-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid adapter ownership.");
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => Guid.TryParse(x.Id, out var current) && current == id);
        if (nic == null) return;
        if (nic.Name != name) throw new InvalidOperationException("Adapter ownership mismatch; no device was removed.");
        var networkClass = new Guid("4d36e972-e325-11ce-bfc1-08002be10318");
        IntPtr set = SetupDiGetClassDevs(ref networkClass, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref device)) break;
                IntPtr handle = SetupDiOpenDevRegKey(set, ref device, 1, 0, 2, 0x20019);
                if (handle == new IntPtr(-1)) continue;
                using var safe = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle, true);
                using var key = RegistryKey.FromHandle(safe);
                if (!Guid.TryParse(key.GetValue("NetCfgInstanceId") as string, out var current) || current != id) continue;
                var remove = new RemoveParameters { HeaderSize = 8, Function = 5, Scope = 1 };
                if (!SetupDiSetClassInstallParams(set, ref device, ref remove, Marshal.SizeOf<RemoveParameters>()) || !SetupDiCallClassInstaller(5, set, ref device)) throw new Win32Exception();
                return;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo info, uint scope, uint profile, uint keyType, uint access);
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid Class; public uint DevInst; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct RemoveParameters { public uint HeaderSize, Function, Scope, HardwareProfile; }
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr guid, IntPtr window);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiOpenDeviceInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfo(IntPtr set, string instance, IntPtr window, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true, EntryPoint = "SetupDiSetClassInstallParamsW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParams(IntPtr set, ref DeviceInfo device, ref RemoveParameters parameters, int size);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfo device);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
