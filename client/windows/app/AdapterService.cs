using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PersonalVpn;

// Device lifetime is independent of every GUI / protocol / capture session.
public static class AdapterService
{
    public const string ServiceName = "PersonalVPNAdapter";
    public const string AdapterName = "PersonalVPN";
    public static Guid RequestedId => new(MD5.HashData(Encoding.UTF8.GetBytes("wintun" + AdapterName)));
    private static readonly ManualResetEvent stop = new(false);
    private static readonly MainCallback main = ServiceMain;
    private static readonly ControlCallback control = HandleControl;
    private static IntPtr statusHandle;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void MainCallback(uint argc, IntPtr argv);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ControlCallback(uint command);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry
    { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public IntPtr Main; }
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint Type, State, Accepted, Exit, SpecificExit, Checkpoint, WaitHint; }
    private static void SetStatus(uint state, uint error = 0)
    {
        var status = new Status { Type = 0x10, State = state, Accepted = state == 4 ? 5u : 0u, Exit = error, WaitHint = state is 2 or 3 ? 30000u : 0u };
        SetServiceStatus(statusHandle, ref status);
    }
    public static int Run()
    {
        var entries = new[] { new Entry { Name = ServiceName, Main = Marshal.GetFunctionPointerForDelegate(main) }, new Entry() };
        bool result = StartServiceCtrlDispatcher(entries); GC.KeepAlive(main); GC.KeepAlive(control);
        return result ? 0 : Marshal.GetLastWin32Error();
    }
    private static WintunAdapter OpenOrCreate()
    {
        try { return new WintunAdapter(AdapterName, RequestedId, openExisting: true); }
        catch (Win32Exception) { return new WintunAdapter(AdapterName, RequestedId); }
    }
    private static void ServiceMain(uint argc, IntPtr argv)
    {
        statusHandle = RegisterServiceCtrlHandler(ServiceName, control);
        if (statusHandle == IntPtr.Zero) return;
        // Wintun may start its kernel driver through SCM. Release the SCM start
        // lock before making that system call; readiness is checked by opening it.
        SetStatus(4);
        WintunAdapter? adapter = null;
        try
        {
            adapter = OpenOrCreate();
            while (!stop.WaitOne(5000))
                if (!WintunAdapter.Exists(adapter.AdapterId)) { adapter.Dispose(); adapter = OpenOrCreate(); }
            SetStatus(3);
            adapter.Dispose(); adapter = null; SetStatus(1);
        }
        catch { adapter?.Dispose(); SetStatus(1, 31); }
    }
    private static void HandleControl(uint command)
    {
        if (command is 1 or 5) { SetStatus(3); stop.Set(); }
        else if (command == 4) SetStatus(4);
    }
    public static uint InstallAndStart()
    {
        if (!WintunAdapter.IsAdministrator) throw new InvalidOperationException("Administrator required to install the adapter service.");
        IntPtr manager = OpenSCManager(null, null, 3);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        IntPtr service = IntPtr.Zero;
        try
        {
            string path = "\"" + Environment.ProcessPath + "\" --adapter-service";
            service = OpenService(manager, ServiceName, 0xF01FF);
            if (service == IntPtr.Zero)
            {
                if (Marshal.GetLastWin32Error() != 1060) throw new Win32Exception();
                service = CreateService(manager, ServiceName, "Personal VPN persistent adapter", 0xF01FF, 0x10, 2, 1, path, null, IntPtr.Zero, null, null, null);
                if (service == IntPtr.Zero) throw new Win32Exception();
            }
            else
            {
                string? previous = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PersonalVPNAdapter", "ImagePath", null) as string;
                if (!string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        if (nic.Name == AdapterName && System.Linq.Enumerable.Any(nic.GetIPProperties().UnicastAddresses, x => x.Address.ToString() == "172.28.255.1"))
                            throw new InvalidOperationException("Disconnect the active TUN session before updating the adapter service.");
                    StopService(service);
                }
                if (!ChangeServiceConfig(service, 0xffffffff, 2, 0xffffffff, path, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception();
            }
            Start(service);
        }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        using var adapter = WaitReady(); return adapter.DriverVersion;
    }
    public static void StopForUpgrade()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            if (nic.Name == AdapterName && System.Linq.Enumerable.Any(nic.GetIPProperties().UnicastAddresses, x => x.Address.ToString() == "172.28.255.1"))
                throw new InvalidOperationException("Disconnect the active Personal VPN TUN session before updating its service.");
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        IntPtr service = OpenService(manager, ServiceName, 0x24);
        try
        {
            if (service == IntPtr.Zero) { if (Marshal.GetLastWin32Error() != 1060) throw new Win32Exception(); return; }
            StopService(service);
        }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }
    private static void StopService(IntPtr service)
    {
        uint previousPid = 0;
        if (QueryServiceStatusEx(service, 0, out var processStatus, Marshal.SizeOf<ProcessStatus>(), out _)) previousPid = processStatus.Pid;
        bool requested = false;
        for (int i = 0; i < 600; i++)
        {
            if (!QueryServiceStatus(service, out var status)) throw new Win32Exception();
            if (status.State == 1)
            {
                // A stopped status can precede process teardown. Release the old
                // native device owner completely before starting its replacement.
                if (previousPid != 0)
                    try { using var process = Process.GetProcessById((int)previousPid); if (!process.WaitForExit(10000)) throw new InvalidOperationException("Previous adapter service process did not exit."); } catch (ArgumentException) { }
                return;
            }
            if (!requested && status.State is 4 or 7)
            {
                if (!ControlService(service, 1, out _))
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code != 1061 && code != 1062) throw new Win32Exception(code);
                }
                requested = true;
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Adapter service stop timed out.");
    }
    public static void Uninstall()
    {
        string? path = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PersonalVPNAdapter", "ImagePath", null) as string;
        if (!string.Equals(path, "\"" + Environment.ProcessPath + "\" --adapter-service", StringComparison.OrdinalIgnoreCase)) return; // An older uninstaller must not stop a newer installation.
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        IntPtr service = OpenService(manager, ServiceName, 0x10024);
        try { if (service == IntPtr.Zero) throw new Win32Exception(); StopService(service); if (!DeleteService(service)) throw new Win32Exception(); }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
    }
    public static void EnsureRunning()
    {
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        IntPtr service = IntPtr.Zero;
        try
        {
            service = OpenService(manager, ServiceName, 0x14);
            if (service == IntPtr.Zero) throw new InvalidOperationException("Install the Windows setup package to prepare the persistent adapter service.");
            Start(service);
        }
        finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        using var adapter = WaitReady();
    }
    private static void Start(IntPtr service)
    {
        if (!StartService(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception();
    }
    private static WintunAdapter WaitReady()
    {
        for (int i = 0; i < 450; i++)
        {
            try { return new WintunAdapter(AdapterName, RequestedId, openExisting: true); }
            catch (Win32Exception) { Thread.Sleep(100); }
        }
        throw new InvalidOperationException("The persistent adapter service did not prepare its device.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessStatus { public Status Status; public uint Pid, Flags; }
    public static uint ProcessId
    {
        get
        {
            IntPtr manager = OpenSCManager(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception();
            IntPtr service = OpenService(manager, ServiceName, 4);
            try
            {
                if (service == IntPtr.Zero || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ProcessStatus>(), out _)) throw new Win32Exception();
                return status.Pid;
            }
            finally { if (service != IntPtr.Zero) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
    }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, uint level, out ProcessStatus status, int size, out int needed);
    public static Guid CurrentId()
    {
        using var adapter = new WintunAdapter(AdapterName, RequestedId, openExisting: true);
        return adapter.AdapterId;
    }
    [DllImport("advapi32.dll", EntryPoint = "StartServiceCtrlDispatcherW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcher([In] Entry[] entries);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr RegisterServiceCtrlHandler(string name, ControlCallback callback);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle, ref Status status);
    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string path, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(IntPtr service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(IntPtr service, uint control, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out Status status);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteService(IntPtr service);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
