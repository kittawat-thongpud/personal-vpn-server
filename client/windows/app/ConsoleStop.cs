using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PersonalVpn;

// Go's signal handler performs TUN address/route/device teardown. A hidden
// private console lets us send SIGINT before resorting to a forced termination.
internal static class ConsoleStop
{
    public static bool Try(Process process)
    {
        if (GetConsoleWindow() != IntPtr.Zero || !AttachConsole((uint)process.Id)) return false;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            return GenerateConsoleCtrlEvent(0, 0) && process.WaitForExit(5000);
        }
        finally { FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); }
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
