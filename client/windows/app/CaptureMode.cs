using System;
using System.IO;

namespace PersonalVpn;
public static class CaptureCapabilities
{
    public static bool TunAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, "cores", "sing-box.exe")) && File.Exists(Path.Combine(AppContext.BaseDirectory, "cores", "wintun.dll"));
    public const string TunNotice = "The TUN components are missing. Reinstall the Windows setup package.";
    public static void EnsureAvailable(string mode)
    {
        if (mode != "Proxy" && mode != "TUN") throw new InvalidOperationException("Unknown capture mode.");
        if (mode == "TUN")
        {
            if (!TunAvailable) throw new InvalidOperationException(TunNotice);
            _ = CoreIntegrity.Verify("sing-box.exe"); _ = CoreIntegrity.Verify("wintun.dll");
            if (!WintunAdapter.IsAdministrator) throw new InvalidOperationException("Open Personal VPN as Administrator to use TUN.");
        }
    }
}