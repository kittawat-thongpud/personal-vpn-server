using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PersonalVpn;

internal static class ShellIdentity
{
    internal const string AppId = "PersonalVPN.Client";
    internal static void Initialize() => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(AppId));
    internal static void RefreshShortcuts()
    {
        foreach (string path in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Personal VPN.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Personal VPN.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Personal VPN", "Personal VPN.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Personal VPN.lnk") })
            if (File.Exists(path)) SHChangeNotify(0x2000, 0x5, path, IntPtr.Zero); // UPDATEITEM, PATHW
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint action, uint flags, string item, IntPtr other);
}
