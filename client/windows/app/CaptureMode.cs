using System;

namespace PersonalVpn;

public static class CaptureCapabilities
{
    // Availability is a runtime capability, never a user-editable boolean.
    // Enable only after the elevated adapter host and recovery are qualified.
    public static bool TunAvailable => false;
    public const string TunNotice = "TUN runtime is not installed in this build. No adapter, route or DNS changes will be made. Proxy remains active.";
    public static void EnsureAvailable(string mode)
    {
        if (mode != "Proxy" && mode != "TUN") throw new InvalidOperationException("Unknown capture mode.");
        if (mode == "TUN" && !TunAvailable) throw new InvalidOperationException(TunNotice);
    }
}
