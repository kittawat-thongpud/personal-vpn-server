package dev.personalvpn.client

import android.net.*
import android.os.CancellationSignal
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.TimeoutCancellationException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.UnknownHostException
import java.net.SocketTimeoutException
import java.util.concurrent.Executor
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

internal data class PhysicalNetwork(val network: Network, val capabilities: NetworkCapabilities,
    val links: LinkProperties, val choice: NetworkChoice)
internal data class NetworkEpoch(val generation: Long = 0, val physical: PhysicalNetwork? = null)

// Callback-provided snapshots avoid racing repeated synchronous CM queries.
internal class PhysicalNetworks(private val changed: (List<PhysicalNetwork>) -> Unit) : ConnectivityManager.NetworkCallback() {
    private data class Entry(val network: Network, var caps: NetworkCapabilities? = null, var links: LinkProperties? = null)
    private val entries = mutableMapOf<Network, Entry>()
    @Synchronized override fun onAvailable(network: Network) { entries.getOrPut(network) { Entry(network) } }
    @Synchronized override fun onLost(network: Network) { entries.remove(network); publish() }
    @Synchronized override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
        val entry = entries[network] ?: return
        entry.caps = NetworkCapabilities(capabilities); publish()
    }
    @Synchronized override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) {
        val entry = entries[network] ?: return
        entry.links = LinkProperties(linkProperties); publish()
    }
    private fun publish() {
        changed(entries.values.mapNotNull { entry ->
            val caps = entry.caps ?: return@mapNotNull null
            val links = entry.links ?: return@mapNotNull null
            if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) || !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) || links.interfaceName.isNullOrBlank()) return@mapNotNull null
            val path = "${entry.network}/${links.interfaceName}/${links.linkAddresses.map { it.toString() }.sorted()}/${links.routes.map { it.toString() }.sorted()}/${links.dnsServers.mapNotNull { it.hostAddress }.sorted()}/${links.mtu}"
            PhysicalNetwork(entry.network, caps, links, NetworkChoice(entry.network.toString(), path,
                caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED), !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_SUSPENDED),
                caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI), !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)))
        })
    }
}

internal suspend fun resolveEndpoint(network: Network, host: String): InetAddress {
    if (InetAddresses.isNumericAddress(host)) return InetAddresses.parseNumericAddress(host)
    return try { withTimeout(6000) {
        suspendCancellableCoroutine { continuation ->
            val signal = CancellationSignal()
            continuation.invokeOnCancellation { signal.cancel() }
            DnsResolver.getInstance().query(network, host, DnsResolver.FLAG_EMPTY, Executor { it.run() }, signal,
                object : DnsResolver.Callback<List<InetAddress>> {
                    override fun onAnswer(answer: List<InetAddress>, rcode: Int) {
                        if (!continuation.isActive) return
                        val selected = answer.firstOrNull { it is Inet4Address } ?: answer.firstOrNull()
                        if (rcode == 0 && selected != null) continuation.resume(selected) else continuation.resumeWithException(UnknownHostException("Endpoint DNS failed"))
                    }
                    override fun onError(error: DnsResolver.DnsException) { if (continuation.isActive) continuation.resumeWithException(UnknownHostException("Endpoint DNS failed")) }
                })
        }
    } } catch (_: TimeoutCancellationException) { throw SocketTimeoutException("Endpoint DNS timeout") }
}
