package dev.personalvpn.client

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.SystemClock
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import org.json.JSONObject
import java.net.InetSocketAddress

internal data class LatencyTarget(val host: String,val port: Int) {
    val key: String get() = "$host:$port"
}
internal fun Profile.latencyTarget(protocol: String="REALITY"): LatencyTarget? = runCatching {
    // One TCP reachability measurement per endpoint. This is not a QUIC or VPN health check.
    val o=JSONObject((if(protocol=="REALITY") reality ?: hysteria else hysteria ?: reality) ?: error("No endpoint"))
    LatencyTarget(o.getString("server"),o.getInt("port"))
}.getOrNull()
internal data class LatencyResult(val milliseconds: Long?=null,val checked: Long=0,val pending: Boolean=false,val reason: String="Not checked",val network: String="") {
    val label: String get()=if(pending) "…" else milliseconds?.let { "$it ms" } ?: if(checked==0L) "—" else if(reason=="Timeout") "Timeout" else "Unavailable"
    val detail: String get()="TCP connect · physical network\n${if(checked==0L) "Not checked" else java.text.DateFormat.getTimeInstance().format(java.util.Date(checked))}\n$reason\nTCP reachability only; Hysteria 2 uses UDP."
}

/** UI-only foreground controller. Never starts a core, changes routes, or drives recovery. */
internal class ServerLatency(context: Context) {
    private val manager=context.getSystemService(ConnectivityManager::class.java)
    private val mutable=MutableStateFlow<Map<String,LatencyResult>>(emptyMap())
    val results=mutable.asStateFlow()
    private val workers=Semaphore(2)
    private val running=mutableMapOf<String,Job>()
    private var scope: CoroutineScope?=null
    private var targets: List<LatencyTarget> = emptyList()
    private var observed=emptyList<PhysicalNetwork>()
    private var callback: PhysicalNetworks?=null
    private var lastNetwork=""
    private fun physical(): Network? {
        val choice=chooseNetwork(observed.map { it.choice }.filterNot { it.suspended },null)
        return observed.find { it.choice.id==choice?.id }?.network
    }
    private fun networkKey(n: Network?)=observed.find { it.network==n }?.choice?.path ?: "offline"
    fun profiles(profiles: List<Profile>,protocol: String) {
        targets=profiles.mapNotNull { it.latencyTarget(protocol) }.distinct()
        running.keys.filter { key -> targets.none { it.key==key } }.forEach { running.remove(it)?.cancel() }
        mutable.value=mutable.value.filterKeys { key -> targets.any { it.key==key } }
        refresh(false)
    }
    fun start() {
        if(scope!=null)return
        val owner=CoroutineScope(SupervisorJob()+Dispatchers.Main.immediate);scope=owner
        val observer=PhysicalNetworks { values -> owner.launch { observed=values;refresh(false) } }
        callback=observer
        manager.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).build(),observer)
        owner.launch { while(isActive) { refresh(false);delay(300_000) } }
    }
    fun stop() {
        callback?.let { runCatching { manager.unregisterNetworkCallback(it) } };callback=null
        scope?.cancel();scope=null;running.clear();observed=emptyList()
        mutable.value=mutable.value.mapValues { (_,v)->v.copy(pending=false) }
    }
    fun refresh(force: Boolean=true,only: LatencyTarget?=null) {
        val owner=scope ?: return
        val network=physical();val key=networkKey(network)
        if(key!=lastNetwork) {
            lastNetwork=key;running.values.toList().forEach { it.cancel() };running.clear()
            mutable.value=emptyMap()
        }
        for(target in (only?.let { listOf(it) } ?: targets)) {
            val previous=mutable.value[target.key]
            if(running[target.key]?.isActive==true)continue
            if(!force && previous!=null && previous.network==key && System.currentTimeMillis()-previous.checked<300_000)continue
            mutable.value=mutable.value+(target.key to (previous?:LatencyResult()).copy(pending=true))
            running[target.key]=owner.launch {
                try {
                    workers.withPermit {
                        var elapsed: Long?=null;var reason="TCP connected"
                        try {
                            withTimeout(3000) {
                                requireNotNull(network)
                                val deadline=SystemClock.elapsedRealtime()+3000
                                val address=resolveEndpoint(network,target.host)
                                runInterruptible(Dispatchers.IO) {
                                    network.socketFactory.createSocket().use { socket ->
                                        val start=SystemClock.elapsedRealtimeNanos()
                                        socket.connect(InetSocketAddress(address,target.port),(deadline-SystemClock.elapsedRealtime()).coerceIn(1,3000).toInt())
                                        elapsed=((SystemClock.elapsedRealtimeNanos()-start)/1_000_000).coerceAtLeast(1)
                                    }
                                }
                            }
                        } catch(_: TimeoutCancellationException) { reason="Timeout" }
                        catch(e: CancellationException) { throw e }
                        catch(_: java.net.SocketTimeoutException) { reason="Timeout" }
                        catch(_: Exception) { reason="TCP unavailable" }
                        if(networkKey(physical())==key) mutable.value=mutable.value+(target.key to LatencyResult(elapsed,System.currentTimeMillis(),false,reason,key))
                        else mutable.value=mutable.value+(target.key to LatencyResult(reason="Network changed"))
                    }
                } finally { if(running[target.key]===currentCoroutineContext()[Job]) running.remove(target.key) }
            }
        }
    }
}
