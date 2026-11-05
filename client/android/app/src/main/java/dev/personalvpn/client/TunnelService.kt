package dev.personalvpn.client

import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.*
import android.os.Build
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import androidx.core.content.ContextCompat
import dev.personalvpn.personalvpn.Core
import dev.personalvpn.personalvpn.Personalvpn
import dev.personalvpn.personalvpn.Platform
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface

class TunnelService : VpnService() {
    companion object {
        const val CONNECT = "dev.personalvpn.CONNECT"
        const val DISCONNECT = "dev.personalvpn.DISCONNECT"
        const val RECONNECT = "dev.personalvpn.RECONNECT"
        fun command(context: android.content.Context, action: String) { ContextCompat.startForegroundService(context, Intent(context, TunnelService::class.java).setAction(action)) }
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lifecycle = Repository.serviceLifecycle
    private var serviceGeneration = -1L
    private val ownsSession get() = Repository.serviceGeneration.get() == serviceGeneration
    private val control = Any()
    private var worker: Job? = null
    private var ticker: Job? = null
    private var cycle: Job? = null
    @Volatile private var core: Core? = null
    @Volatile private var tun: ParcelFileDescriptor? = null
    @Volatile private var strictApps = false
    @Volatile private var desired = false
    @Volatile private var destroyed = false
    @Volatile private var networkEpoch = NetworkEpoch()
    @Volatile private var physicalNetworks = emptyList<PhysicalNetwork>()
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val cm by lazy { getSystemService(ConnectivityManager::class.java) }
    private val monitor = PhysicalNetworks(::networksChanged)
    @Volatile private var baseUp = 0L
    @Volatile private var baseDown = 0L
    @Volatile private var trafficEpoch = 0L
    private val platform = object : Platform {
        override fun openTun(mtu: Int) = this@TunnelService.openTun(mtu)
        override fun protect(fd: Int) = protectSocket(fd)
        override fun owner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int) = this@TunnelService.owner(protocol, source, sourcePort, destination, destinationPort)
        override fun interfaces() = this@TunnelService.interfaces()
        override fun defaultInterface() = this@TunnelService.defaultInterface()
    }
    override fun onCreate() {
        super.onCreate(); serviceGeneration = Repository.serviceGeneration.incrementAndGet(); Surfaces.channels(this)
        cm.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), monitor)
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (destroyed) return START_NOT_STICKY
        if (prepare(this) != null) { Repository.log("WARN", "vpn.consent_required"); stopSelf(); return START_NOT_STICKY }
        val notification = Surfaces.notification(this)
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED) else startForeground(1, notification)
        if (intent?.action == DISCONNECT) {
            if (isAlwaysOn) {
                Repository.log("WARN", "disconnect.managed", "Always-on is managed in Android VPN settings.")
                mutateState { it.copy(detail = "Always-on is managed in Android VPN settings.") }; Surfaces.refresh(this); return START_STICKY
            }
            desired = false; cancelPending(); worker?.cancel()
            worker = scope.launch { lifecycle.withLock {
                closeCore(); closeCapture(); update("Disconnected", false, false)
                withContext(Dispatchers.Main) { if (!desired) { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf(startId) } }
            } }
            return START_NOT_STICKY
        }
        // Sticky starts and ordinary Connect may also recover a completed worker.
        if (desired && worker?.isActive == true && intent?.action != RECONNECT && Repository.state.value.status != "Error") return START_STICKY
        if (!desired && tun == null) {
            baseUp = 0; baseDown = 0; trafficEpoch++
            mutateState { it.copy(exitIp = "—", appliedConfig = "", uploadRate = 0, downloadRate = 0, uploaded = 0, downloaded = 0, history = emptyList()) }
        }
        desired = true; cancelPending(); worker?.cancel()
        worker = scope.launch { lifecycle.withLock {
            val config = Repository.config.value
            strictApps = config.apps.mode != Action.Disabled
            while (currentCoroutineContext().isActive && desired && !destroyed) { try {
                try { EngineConfig.validate(config) } catch (_: Exception) { throw SessionBlocked("configuration") }
                superviseRecovery({ desired && !destroyed }, { runSession(config) }, { attempt, error ->
                    Repository.log("ERROR", "worker.restarting", "Attempt $attempt; reason=${Repository.cause(error)}; capture retained.")
                    update("Reconnecting", true, true, "Recovery worker restarting")
                }, { attempt -> delay(backoff(attempt)) })
            } catch (cancel: CancellationException) { throw cancel }
            catch (blocked: SessionBlocked) {
                if (blocked.category == "configuration") {
                    Repository.log("ERROR", "backend.action_required", blocked.category)
                    update("Error", tun != null, false, "Check profile and backend logs; then Reconnect.")
                    awaitCancellation()
                }
                val failedGeneration = blocked.networkGeneration ?: networkEpoch.generation
                if (networkEpoch.generation != failedGeneration) continue
                Repository.log("WARN", "backend.authentication_wait", "${blocked.category}; retry in 30s or after a network change; capture retained.")
                update("Error", tun != null, false, "${blocked.category}: check profile/clock; retry in 30s or after network change.")
                awaitNetworkChange(wake, 30000) { networkEpoch.generation != failedGeneration }
            } finally { withContext(NonCancellable) { closeCore() } }
            }
        } }
        if (ticker?.isActive != true) ticker = scope.launch { streamTraffic() }
        return START_STICKY
    }
    private fun backoff(attempt: Int) = minOf(30000L, 1000L shl minOf(attempt, 5)) + (0..400).random()
    private fun checkEpoch(expected: NetworkEpoch) {
        if (!desired || destroyed || networkEpoch.generation != expected.generation) throw StaleNetwork()
    }
    private suspend fun runSession(config: Config) {
        var attempt = 0
        var previousGeneration = -1L
        var appliedPath: String? = null
        try {
            while (currentCoroutineContext().isActive && desired) {
                val epoch = networkEpoch
                if (epoch.generation != previousGeneration) {
                    attempt = 0; previousGeneration = epoch.generation
                    delay(250)
                    if (networkEpoch.generation != epoch.generation) continue
                }
                val physical = epoch.physical
                if (physical == null || physical.choice.suspended) {
                    closeCore(); appliedPath = null
                    update("Waiting for network", true, true)
                    awaitNetworkChange(wake, 3000) { networkEpoch.generation != epoch.generation }; continue
                }
                // A real path change uses one controlled transport replacement.
                // Do not notify the interface monitor AND reset the core again.
                if (appliedPath != physical.choice.path) { closeCore(); appliedPath = null }
                var phase = "endpoint_dns"
                try {
                    coroutineScope {
                        val pending = async(start = CoroutineStart.LAZY) {
                            if (core == null) {
                                update(if (attempt == 0) "Connecting" else "Reconnecting", true, true)
                                val resolved = resolveEndpoint(physical.network, config.selectedProfile().endpoint(config.protocol))
                                checkEpoch(epoch)
                                val content = EngineConfig.compile(config, resolved.hostAddress!!, Repository.instrumentedCertificate != null)
                                phase = "core_start"
                                Personalvpn.check(content)
                                checkEpoch(epoch)
                                core = Personalvpn.start(noBackupFilesDir.absolutePath, content, platform)
                                currentCoroutineContext().ensureActive(); checkEpoch(epoch)
                                appliedPath = physical.choice.path
                                mutateState { it.copy(appliedConfig = config.fingerprint(), profileName = config.selectedProfile().name, protocol = config.protocol) }
                                Repository.log("INFO", "core.started", "${config.protocol}; generation=${epoch.generation}")
                            }
                            phase = "health"
                            verifyNetwork(config, epoch) { attempt = 0 }
                        }
                        synchronized(control) {
                            if (networkEpoch.generation != epoch.generation) pending.cancel(StaleNetwork()) else cycle = pending
                        }
                        try { pending.start(); pending.await() } finally { synchronized(control) { if (cycle === pending) cycle = null } }
                    }
                } catch (cancel: CancellationException) {
                    currentCoroutineContext().ensureActive()
                    if (networkEpoch.generation == epoch.generation) throw IOException("Network cycle cancelled unexpectedly", cancel)
                    Repository.log("INFO", "network.stale_discarded", "Discarded work for generation=${epoch.generation}")
                } catch (blocked: SessionBlocked) { throw blocked }
                catch (error: Exception) {
                    if (networkEpoch.generation != epoch.generation) continue
                    attempt++
                    val delayMs = backoff(attempt)
                    Repository.log("WARN", "backend.retry", "Attempt $attempt; generation=${epoch.generation}; stage=$phase; reason=${Repository.cause(error)}; retry in ${delayMs}ms; no Direct fallback.")
                    update("Reconnecting", true, true, "$phase: ${Repository.cause(error)}")
                    closeCore(); appliedPath = null
                    awaitNetworkChange(wake, delayMs) { networkEpoch.generation != epoch.generation }
                }
            }
        } finally { withContext(NonCancellable) { closeCore() } }
    }
    private data class HealthTarget(val label: String, val address: String)
    private fun healthTargets(): List<HealthTarget> = if (Repository.instrumentedCertificate != null) listOf(
        HealthTarget("primary", "https://fixture.local:18445/" + if (Repository.instrumentedHealthFailure > 0) "fail" else "ip"),
        HealthTarget("secondary", "https://fixture.local:18445/" + if (Repository.instrumentedHealthFailure > 1) "fail" else "ip")
    ) else listOf(HealthTarget("primary", "https://api.ipify.org/"), HealthTarget("secondary", "https://checkip.amazonaws.com/"))
    private suspend fun verifyNetwork(config: Config, epoch: NetworkEpoch, healthy: () -> Unit) {
        val failures = HealthFailures()
        var connected = false
        while (currentCoroutineContext().isActive) {
            checkEpoch(epoch)
            val started = SystemClock.elapsedRealtime()
            var result: String? = null
            var lastFailure: Exception = IOException("HTTPS health failed")
            for (target in healthTargets()) {
                checkEpoch(epoch)
                try {
                    if (!connected) update("Verifying", true, true)
                    val current = core ?: throw IOException("Core closed")
                    val certificate = Repository.instrumentedCertificate.orEmpty()
                    val verified = probe(current) { current.verifyAt(target.address, certificate) }
                    checkEpoch(epoch)
                    if (Repository.instrumentedDiagnostics) Repository.log("INFO", "fixture.outbound_verified", verified)
                    val captured = probe(current) { current.verifyCapturedAt(target.address, certificate) }
                    checkEpoch(epoch)
                    check(verified == captured) { "Captured exit differs from authenticated outbound" }
                    result = verified
                    if (target.label == "secondary") Repository.log("INFO", "health.fallback_verified", "Secondary authenticated HTTPS and OS capture agree.")
                    break
                } catch (cancel: CancellationException) { throw cancel }
                catch (error: Exception) {
                    lastFailure = error
                    val reason = Repository.cause(error)
                    Repository.log("WARN", "health.target_failed", "${target.label}; generation=${epoch.generation}; reason=$reason")
                    if (reason in setOf("certificate_pin", "reality_authentication", "authentication")) throw SessionBlocked(reason, epoch.generation)
                }
            }
            checkEpoch(epoch)
            val verifiedExit = result
            if (verifiedExit != null) {
                failures.success()
                healthy()
                if (!connected) Repository.log("INFO", "health.verified", "generation=${epoch.generation}; elapsed=${SystemClock.elapsedRealtime() - started}ms; authenticated HTTPS and OS capture agree.")
                connected = true
                // Commit the result atomically with the generation check.
                synchronized(control) {
                    checkEpoch(epoch)
                    mutateState { it.copy(status = "Connected", active = true, busy = false, exitIp = verifiedExit, detail = config.protocol) }
                }
                Surfaces.refresh(this)
            } else {
                if (failures.failed()) throw IOException("Consecutive HTTPS health checks failed", lastFailure)
                update("Degraded", true, false, "HTTPS verification retry; capture and core retained")
                Repository.log("WARN", "health.degraded", "generation=${epoch.generation}; retry=${failures.count}/2; core retained.")
            }
            delay(if (result == null) 3000 else 15000)
        }
    }
    private suspend fun probe(current: Core, request: () -> String): String = coroutineScope {
        val pending = async(Dispatchers.IO) { request() }
        try { withTimeout(9000) { pending.await() } }
        catch (timeout: TimeoutCancellationException) { throw IOException("HTTPS probe timeout", timeout) }
        finally { if (!pending.isCompleted) { current.cancelPending(); pending.cancel() } }
    }
    private fun cancelPending() {
        synchronized(control) { cycle?.cancel(StaleNetwork()) }
        core?.cancelPending()
        wake.trySend(Unit)
    }
    private fun networksChanged(values: List<PhysicalNetwork>) {
        if (destroyed) return
        physicalNetworks = values
        val choice = chooseNetwork(values.map { it.choice }, networkEpoch.physical?.choice?.id)
        val selected = values.firstOrNull { it.choice == choice }
        val changed: Boolean
        val epoch: NetworkEpoch
        synchronized(control) {
            val previous = networkEpoch
            changed = previous.physical?.choice?.generationKey != selected?.choice?.generationKey
            epoch = NetworkEpoch(previous.generation + if (changed) 1 else 0, selected)
            networkEpoch = epoch
        }
        applyUnderlying(epoch)
        if (changed) {
            val transport = when { selected == null -> "none"; choice?.wifi == true -> "wifi"; selected.capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"; else -> "other" }
            Repository.log("INFO", "network.changed", "generation=${epoch.generation}; transport=$transport; validated=${choice?.validated == true}; TUN retained.")
            cancelPending()
        }
    }
    private fun applyUnderlying(epoch: NetworkEpoch) {
        if (tun == null || !ownsSession) return
        val networks = epoch.physical?.let { arrayOf(it.network) } ?: emptyArray()
        runCatching { check(setUnderlyingNetworks(networks)) }.onFailure { Repository.log("WARN", "network.metadata_failed", it.javaClass.simpleName) }
    }
    private fun closeCore() {
        val previous = core; core = null
        if (previous != null) {
            previous.cancelPending()
            baseUp += previous.upload(); baseDown += previous.download()
            previous.close(); Repository.log("INFO", "core.stopped")
        }
    }
    @Synchronized private fun closeCapture() {
        val previous = tun; tun = null
        runCatching { previous?.close() }.onFailure { Repository.log("WARN", "capture.close_failed", it.javaClass.simpleName) }
        if (ownsSession) runCatching { setUnderlyingNetworks(null) }
        if (previous != null) Repository.log("INFO", "capture.released")
    }
    private fun update(status: String, active: Boolean, busy: Boolean, detail: String = "") {
        if (!ownsSession) return
        if (status != Repository.state.value.status) Repository.log("INFO", "state.${status.lowercase().replace(' ', '_')}", detail)
        mutateState { it.copy(status = status, active = active, busy = busy, detail = detail, exitIp = if (status == "Connected") it.exitIp else "—", uploadRate = if (active) it.uploadRate else 0, downloadRate = if (active) it.downloadRate else 0) }
        Surfaces.refresh(this)
    }
    private fun mutateState(transform: (TunnelState) -> TunnelState) {
        Repository.state.update { if (ownsSession) transform(it) else it }
    }
    private suspend fun streamTraffic() {
        var lastUp = 0L; var lastDown = 0L; var lastTime = SystemClock.elapsedRealtime(); var sessionEpoch = trafficEpoch
        while (currentCoroutineContext().isActive) {
            delay(1000)
            if (sessionEpoch != trafficEpoch) { sessionEpoch = trafficEpoch; lastUp = 0; lastDown = 0; lastTime = SystemClock.elapsedRealtime() - 1000 }
            val current = core
            val up = baseUp + (current?.upload() ?: 0); val down = baseDown + (current?.download() ?: 0)
            val now = SystemClock.elapsedRealtime(); val elapsed = maxOf(1, now - lastTime)
            val upRate = maxOf(0, up - lastUp) * 1000 / elapsed; val downRate = maxOf(0, down - lastDown) * 1000 / elapsed
            mutateState { old -> old.copy(uploadRate = if (old.active) upRate else 0, downloadRate = if (old.active) downRate else 0, uploaded = maxOf(old.uploaded, up), downloaded = maxOf(old.downloaded, down), history = (old.history + (upRate to downRate)).takeLast(60)) }
            if (desired) Surfaces.rate(this)
            lastUp = maxOf(lastUp, up); lastDown = maxOf(lastDown, down); lastTime = now
        }
    }
    @Synchronized private fun openTun(mtu: Int): Int {
        tun?.let { return it.fd }
        val epoch = networkEpoch
        val builder = Builder().setSession("Personal VPN").setMtu(mtu).addAddress("172.29.255.1", 30).addAddress("fd66:706e:1::1", 126).addRoute("0.0.0.0", 0).addRoute("::", 0).addDnsServer("172.29.255.2").setBlocking(false).setMetered(false)
        builder.setUnderlyingNetworks(epoch.physical?.let { arrayOf(it.network) } ?: emptyArray())
        builder.setConfigureIntent(android.app.PendingIntent.getActivity(this, 10, Intent(this, MainActivity::class.java), android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_IMMUTABLE))
        tun = builder.establish() ?: error("Android VPN consent was revoked")
        applyUnderlying(networkEpoch)
        Repository.log("INFO", "capture.established", "IPv4 and IPv6 capture; all app rules run inside the core.")
        return tun!!.fd
    }
    private fun protectSocket(fd: Int) {
        check(super.protect(fd)) { "Socket protection failed" }
        val underlying = networkEpoch.physical?.network ?: error("Underlying network unavailable")
        ParcelFileDescriptor.fromFd(fd).use { underlying.bindSocket(it.fileDescriptor) }
    }
    private fun owner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int): String = try {
        val uid = cm.getConnectionOwnerUid(protocol, InetSocketAddress(InetAddress.getByName(source), sourcePort), InetSocketAddress(InetAddress.getByName(destination), destinationPort))
        val packages = packageManager.getPackagesForUid(uid)?.toList().orEmpty()
        val safe = if (uid < 0 || (strictApps && packages.size != 1)) listOf("__unresolved__") else packages
        JSONObject().put("uid", uid).put("packages", JSONArray(safe)).toString()
    } catch (_: Exception) { "{\"uid\":-1,\"packages\":[\"__unresolved__\"]}" }
    private fun defaultInterface(): String {
        val physical = networkEpoch.physical; val name = physical?.links?.interfaceName.orEmpty()
        return JSONObject().put("name", name).put("index", runCatching { NetworkInterface.getByName(name)?.index ?: -1 }.getOrDefault(-1)).put("expensive", physical?.choice?.metered ?: false).toString()
    }
    private fun interfaces(): String {
        val entries = JSONArray()
        for (physical in physicalNetworks) {
            val links = physical.links; val name = links.interfaceName ?: continue
            val nic = runCatching { NetworkInterface.getByName(name) }.getOrNull() ?: continue
            entries.put(JSONObject().put("index", nic.index).put("name", name).put("mtu", nic.mtu).put("addresses", JSONArray(links.linkAddresses.map { it.toString() })).put("dns", JSONArray(links.dnsServers.map { it.hostAddress })).put("type", if (physical.choice.wifi) 0 else 1).put("metered", physical.choice.metered))
        }
        return entries.toString()
    }
    override fun onRevoke() {
        desired = false; cancelPending(); worker?.cancel()
        Repository.log("WARN", "vpn.revoked", "Android revoked this VPN session")
        scope.launch { lifecycle.withLock { closeCore(); closeCapture(); update("Disconnected", false, false); stopSelf() } }
    }
    override fun onDestroy() {
        destroyed = true; desired = false; cancelPending(); worker?.cancel(); ticker?.cancel()
        runCatching { cm.unregisterNetworkCallback(monitor) }
        scope.launch(start = CoroutineStart.UNDISPATCHED) { lifecycle.withLock { closeCore(); closeCapture(); update("Disconnected", false, false) }; scope.cancel() }
        super.onDestroy()
    }
}
