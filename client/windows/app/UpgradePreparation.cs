using System;
using System.Threading;

namespace PersonalVpn;

internal static class UpgradePreparation
{
    internal static void Prepare()
    {
        if (!WintunAdapter.IsAdministrator) throw new InvalidOperationException("Administrator required to update Personal VPN.");
        if (GuiRunning())
        {
            if (!EventWaitHandle.TryOpenExisting(MainWindow.ExitEventName, out var signal))
                throw new InvalidOperationException("Disconnect and exit the older Personal VPN from its tray menu, then retry Setup.");
            using (signal) signal.Set();
            for (int i = 0; i < 450 && GuiRunning(); i++) Thread.Sleep(100);
            if (GuiRunning()) throw new InvalidOperationException("Personal VPN has not finished disconnecting. Wait and retry Setup.");
        }
        // Restart Manager can also close an older GUI/guard. Recover only our
        // abandoned leases before replacing binaries; never reset another VPN.
        for (int i = 0; i < 100; i++)
        {
            TunLease.RecoverAbandoned(); SystemProxyLease.RecoverAbandoned();
            if (!AdapterService.HasActiveCapture) break;
            Thread.Sleep(100); // Allow an older owned guard to finish restoring.
        }
        AdapterService.StopForUpgrade();
    }
    private static bool GuiRunning()
    {
        if (!Mutex.TryOpenExisting(@"Local\PersonalVPN-" + Environment.UserName, out var mutex)) return false;
        mutex.Dispose(); return true;
    }
}
