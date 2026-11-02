using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace PersonalVpn;

// The service owns the native Create handle. Capture cores open the same device.
// Removal is reserved for one-time legacy migration, never a session lifecycle.
public sealed class WintunAdapter : IDisposable
{
    private IntPtr library, adapter;
    private readonly Close close;
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate IntPtr Create(string name, string tunnelType, ref Guid id);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)] private delegate IntPtr Open(string name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void Close(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Version();
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public WintunAdapter(string name, Guid id, bool openExisting = false)
    {
        if (!IsAdministrator) throw new InvalidOperationException("Run Personal VPN as Administrator.");
        library = NativeLibrary.Load(CoreIntegrity.Verify("wintun.dll"));
        close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "WintunCloseAdapter"));
        var create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "WintunCreateAdapter"));
        adapter = openExisting ? Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "WintunOpenAdapter"))(name) : create(name, "PersonalVPN", ref id);
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
                bool matches;
                using (var safe = new Microsoft.Win32.SafeHandles.SafeRegistryHandle(handle, true))
                using (var key = RegistryKey.FromHandle(safe))
                    matches = Guid.TryParse(key.GetValue("NetCfgInstanceId") as string, out var current) && current == id;
                if (!matches) continue;
                // Close registry handles before asking Windows to uninstall this
                // exact device. Run native cleanup out of process with a deadline.
                var instance = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref device, instance, instance.Capacity, out _)) throw new Win32Exception();
                string system = !Environment.Is64BitProcess && Environment.Is64BitOperatingSystem ? "Sysnative" : "System32";
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), system, "pnputil.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("/remove-device"); info.ArgumentList.Add(instance.ToString());
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Native device cleanup did not start.");
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(); throw new InvalidOperationException("Native device cleanup timed out."); }
                System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { output, error }, 2000);
                if (process.ExitCode != 0) throw new InvalidOperationException("Native device cleanup failed: " + process.ExitCode);
                return;
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo info, uint scope, uint profile, uint keyType, uint access);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
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
