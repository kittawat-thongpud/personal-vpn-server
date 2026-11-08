package dev.personalvpn.client

internal enum class ConnectionAction(val label: String) {
    Connect("Connect"), Cancel("Cancel"), Disconnect("Disconnect"), Apply("Apply & reconnect"), Stopping("Stopping…")
}
internal fun connectionAction(state: TunnelState, dirty: Boolean = false): ConnectionAction = when {
    state.status == "Disconnecting" -> ConnectionAction.Stopping
    state.active && state.busy -> ConnectionAction.Cancel
    state.active && dirty -> ConnectionAction.Apply
    state.active -> ConnectionAction.Disconnect
    else -> ConnectionAction.Connect
}
