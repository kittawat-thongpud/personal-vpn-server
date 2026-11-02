using System;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PersonalVpn;

// A dedicated guard owns the CreateAdapter handle. Closing that handle removes
// this adapter (including its addresses/routes); other adapters are untouched.
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
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}\" + id.ToString("B") + @"\Connection");
        string instance = key?.GetValue("PnpInstanceID") as string ?? throw new InvalidOperationException("Adapter device identity unavailable.");
        if (!instance.StartsWith(@"SWD\WINTUN\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected adapter device identity.");
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var device = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
            if (!SetupDiOpenDeviceInfo(set, instance, IntPtr.Zero, 0, ref device)) throw new Win32Exception();
            var remove = new RemoveParameters { HeaderSize = 8, Function = 5, Scope = 1 };
            if (!SetupDiSetClassInstallParams(set, ref device, ref remove, Marshal.SizeOf<RemoveParameters>()) || !SetupDiCallClassInstaller(5, set, ref device)) throw new Win32Exception();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
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
