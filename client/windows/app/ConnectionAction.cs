namespace PersonalVpn;

internal enum ConnectionAction { Connect, Cancel, Disconnect, Apply, ReclaimProxy, Stopping }

internal static class ConnectionActions
{
    internal static ConnectionAction Current(string status, bool running, bool starting, bool stopping, bool dirty)
    {
        if (stopping || status == "Disconnecting") return ConnectionAction.Stopping;
        if (starting || (running && status is ("Connecting" or "Verifying" or "Reconnecting" or "Waiting for network" or "Degraded")))
            return ConnectionAction.Cancel;
        if (!running) return ConnectionAction.Connect;
        if (dirty) return ConnectionAction.Apply;
        return status == "Proxy conflict" ? ConnectionAction.ReclaimProxy : ConnectionAction.Disconnect;
    }

    internal static bool Enabled(ConnectionAction action, bool hasProfile) => action switch
    {
        ConnectionAction.Stopping => false,
        ConnectionAction.Connect or ConnectionAction.Apply => hasProfile,
        _ => true
    };

    internal static string Label(ConnectionAction action) => action switch
    {
        ConnectionAction.Apply => "Apply & reconnect",
        ConnectionAction.ReclaimProxy => "Use proxy",
        ConnectionAction.Stopping => "Stopping…",
        _ => action.ToString()
    };
}
