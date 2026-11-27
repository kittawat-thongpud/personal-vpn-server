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
    @Volatile private var appRulesEnabled = false
    @Volatile private var ownerNotices = OwnerNotices()
    @Volatile private var desired = false
    @Volatile private var destroyed = false
    private val commandGeneration = java.util.concurrent.atomic.AtomicLong()
    @Volatile private var networkEpoch = NetworkEpoch()
    @Volatile private var physicalNetworks = emptyList<PhysicalNetwork>()
    private val underlaySelector = UnderlaySelector()
    private var selectionLogKey = ""
    private var networkRecoveryStarted = 0L
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
            stopTunnel("user",startId)
            return if(isAlwaysOn)START_STICKY else START_NOT_STICKY
        }
        // Sticky starts and ordinary Connect may also recover a completed worker.
        if (desired && worker?.isActive == true && intent?.action != RECONNECT && Repository.state.value.status != "Error") return START_STICKY
        if (!desired && tun == null) {
            baseUp = 0; baseDown = 0; trafficEpoch++
            mutateState { it.copy(exitIp = "—", appliedConfig = "", uploadRate = 0, downloadRate = 0, uploaded = 0, downloaded = 0, history = emptyList()) }
        }
        val command=commandGeneration.incrementAndGet()
        desired = true; cancelPending(); worker?.cancel()
        synchronized(control) { underlaySelector.reset(); selectionLogKey = ""; networkRecoveryStarted = 0L }
        update("Connecting",true,true,"Preparing VPN connection")
        worker = scope.launch { lifecycle.withLock {
            if(command!=commandGeneration.get() || !ownsSession || !desired) return@withLock
            val config = Repository.config.value
            appRulesEnabled = config.apps.mode != Action.Disabled
            ownerNotices = OwnerNotices()
            while (currentCoroutineContext().isActive && desired && !destroyed) { try {
                try { EngineConfig.validate(config) } catch (_: Exception) { throw SessionBlocked("configuration") }
                superviseRecovery({ desired && !destroyed && ownsSession && command == commandGeneration.get() }, { runSession(config, command) }, { attempt, error ->
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
    private fun stopTunnel(source: String,startId: Int? = null, revoked: Boolean = false) {
        if (isAlwaysOn && !revoked) {
            Repository.log("WARN","disconnect.managed","Always-on is managed in Android VPN settings.")
            mutateState { it.copy(detail="Always-on is managed in Android VPN settings.") };Surfaces.refresh(this);return
        }
        if(!desired && Repository.state.value.status=="Disconnecting")return
        val command=commandGeneration.incrementAndGet()
        desired=false
        update("Disconnecting",true,true,"Stopping VPN and releasing capture")
        Repository.log("INFO","backend.stop_requested","source=$source; cancelling pending connection work")
        cancelPending();worker?.cancel()
        worker=scope.launch { lifecycle.withLock {
            if(command!=commandGeneration.get() || !ownsSession)return@withLock
            withContext(NonCancellable) {
                var cleanupFailed = false
                try { releaseSessionResources(::closeCore, ::closeCapture) }
                catch (error: Exception) {
                    cleanupFailed = true
                    Repository.log("ERROR", "backend.stop_cleanup_failed", "reason=${Repository.cause(error)}; capture release attempted.")
                }
                if(command==commandGeneration.get() && !desired) {
                    update("Disconnected",false,false)
                    Repository.log(if (cleanupFailed) "WARN" else "INFO", "backend.stopped",
                        if (cleanupFailed) "Stop completed with cleanup errors; foreground service stopping. Inspect cleanup events."
                        else "Core and capture released; foreground service stopping.")
                    withContext(Dispatchers.Main) {
                        if(command==commandGeneration.get() && !desired) {
                            stopForeground(STOP_FOREGROUND_REMOVE)
                            if(startId==null)stopSelf()else stopSelf(startId)
                        }
                    }
                }
            }
        } }
    }
    override fun onTaskRemoved(rootIntent: Intent?) {
        stopTunnel("task_removed")
        super.onTaskRemoved(rootIntent)
    }
    private fun backoff(attempt: Int) = minOf(30000L, 1000L shl minOf(attempt, 5)) + (0..400).random()
    private fun checkEpoch(expected: NetworkEpoch) {
        if (!desired || destroyed || networkEpoch.generation != expected.generation) throw StaleNetwork()
    }
    private fun recoveryPhase(epoch: Long, attempt: Int, phase: String, started: Long, outcome: String) {
        Repository.log(if (outcome == "failed") "WARN" else "INFO", "backend.phase",
            "generation=$epoch; attempt=$attempt; phase=$phase; elapsed_ms=${SystemClock.elapsedRealtime() - started}; result=$outcome")
    }

    private suspend fun <T> measurePhase(epoch: NetworkEpoch, attempt: Int, phase: String, operation: suspend () -> T): T {
        val started = SystemClock.elapsedRealtime()
        var result = "failed"
        try {
            val value = operation()
            result = "ok"
            return value
        } catch (cancel: CancellationException) {
            result = "cancelled"
            throw cancel
        } finally {
            recoveryPhase(epoch.generation, attempt, phase, started, result)
        }
    }
    private suspend fun runSession(config: Config, command: Long) {
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
                                if(Repository.instrumentedDiagnostics && Repository.instrumentedConnectHold)awaitCancellation()
                                val resolved = measurePhase(epoch, attempt, "endpoint_dns") {
                                    resolveEndpoint(physical.network, config.selectedProfile().endpoint(config.protocol))
                                }
                                checkEpoch(epoch)
                                val content = EngineConfig.compile(config, resolved.hostAddress!!, Repository.instrumentedCertificate != null)
                                phase = "core_start"
                                measurePhase(epoch, attempt, "transport_start") {
                                    Personalvpn.check(content)
                                    checkEpoch(epoch)
                                    core = Personalvpn.start(noBackupFilesDir.absolutePath, content, platform)
                                }
                                currentCoroutineContext().ensureActive(); checkEpoch(epoch)
                                appliedPath = physical.choice.path
                                mutateState { it.copy(appliedConfig = config.fingerprint(), profileName = config.selectedProfile().name, protocol = config.protocol) }
                                Repository.log("INFO", "core.started", "${config.protocol}; generation=${epoch.generation}; client=${BuildConfig.VERSION_NAME}")
                            }
                            phase = "health"
                            verifyNetwork(config, epoch, attempt) { attempt = 0 }
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
                    currentCoroutineContext().ensureActive()
                    if (!desired || destroyed || !ownsSession || command != commandGeneration.get()) return
                    if (networkEpoch.generation != epoch.generation) continue
                    attempt++
                    val delayMs = backoff(attempt)
                    Repository.log("WARN", "backend.retry", "Attempt $attempt; generation=${epoch.generation}; stage=$phase; reason=${Repository.cause(error)}; retry in ${delayMs}ms; no Direct fallback.")
                    update("Reconnecting", true, true, "$phase: ${Repository.cause(error)}")
                    closeCore(); appliedPath = null
                    val retryStarted = SystemClock.elapsedRealtime()
                    awaitNetworkChange(wake, delayMs) {
                        val latest = networkEpoch
                        latest.generation != epoch.generation ||
                            (!physical.choice.validated && latest.physical?.choice?.validated == true)
                    }
                    val latest = networkEpoch
                    val wakeReason = when {
                        latest.generation != epoch.generation -> "generation_changed"
                        !physical.choice.validated && latest.physical?.choice?.validated == true -> "validation_returned"
                        else -> null
                    }
                    if (wakeReason != null) Repository.log("INFO", "backend.retry_woken",
                        "generation=${latest.generation}; previous_generation=${epoch.generation}; reason=$wakeReason; elapsed_ms=${SystemClock.elapsedRealtime() - retryStarted}; stage=$phase.")
                }
            }
        } finally { withContext(NonCancellable) { closeCore() } }
    }
    private data class HealthTarget(val label: String, val address: String)
    private fun healthTargets(): List<HealthTarget> = if (Repository.instrumentedCertificate != null) listOf(
        HealthTarget("primary", "https://fixture.local:18445/" + if (Repository.instrumentedSlowHealth) "slow" else if (Repository.instrumentedHealthFailure > 0) "fail" else "ip"),
        HealthTarget("secondary", "https://fixture.local:18445/" + if (Repository.instrumentedSlowHealth) "slow" else if (Repository.instrumentedHealthFailure > 1) "fail" else "ip")
    ) else listOf(HealthTarget("primary", "https://api.ipify.org/"), HealthTarget("secondary", "https://checkip.amazonaws.com/"))
    private suspend fun verifyNetwork(config: Config, epoch: NetworkEpoch, attempt: Int, healthy: () -> Unit) {
        val failures = HealthFailures()
        var connected = false
        var degradedAt = 0L
        checkEpoch(epoch)
        update("Verifying", true, true)
        while (currentCoroutineContext().isActive) {
            checkEpoch(epoch)
            val started = SystemClock.elapsedRealtime()
            var result: String? = null
            var lastFailure: Exception = IOException("HTTPS health failed")
            for (target in healthTargets()) {
                checkEpoch(epoch)
                val probeStarted = SystemClock.elapsedRealtime()
                var phase = "outbound"
                try {
                    val current = core ?: throw IOException("Core closed")
                    val certificate = Repository.instrumentedCertificate.orEmpty()
                    val report = !connected || degradedAt != 0L
                    val verified = if (report) measurePhase(epoch, attempt, "outbound_https") {
                        probe(current) { current.verifyAt(target.address, certificate) }
                    } else probe(current) { current.verifyAt(target.address, certificate) }
                    checkEpoch(epoch)
                    if (Repository.instrumentedDiagnostics) Repository.log("INFO", "fixture.outbound_verified", verified)
                    phase = "capture"
                    val captured = if (report) measurePhase(epoch, attempt, "captured_https") {
                        probe(current) { current.verifyCapturedAt(target.address, certificate) }
                    } else probe(current) { current.verifyCapturedAt(target.address, certificate) }
                    checkEpoch(epoch)
                    phase = "exit_comparison"
                    check(verified == captured) { "Captured exit differs from authenticated outbound" }
                    result = verified
                    if (target.label == "secondary") Repository.log("INFO", "health.fallback_verified", "Secondary authenticated HTTPS and OS capture agree.")
                    break
                } catch (cancel: CancellationException) { throw cancel }
                catch (error: Exception) {
                    currentCoroutineContext().ensureActive();checkEpoch(epoch)
                    lastFailure = error
                    val reason = Repository.cause(error)
                    val elapsed = SystemClock.elapsedRealtime() - probeStarted
                    Repository.log("WARN", "health.target_failed",
                        "target=${target.label}; generation=${epoch.generation}; phase=$phase; reason=$reason; elapsed=${elapsed}ms")
                    if (reason in setOf("certificate_pin", "reality_authentication", "authentication")) throw SessionBlocked(reason, epoch.generation)
                }
            }
            checkEpoch(epoch)
            val verifiedExit = result
            if (verifiedExit != null) {
                failures.success()
                // Commit the result atomically with the generation check.
                synchronized(control) {
                    checkEpoch(epoch)
                    healthy()
                    epoch.physical?.choice?.let(underlaySelector::verified)
                    if (!connected) Repository.log("INFO", "health.verified", "generation=${epoch.generation}; elapsed=${SystemClock.elapsedRealtime() - started}ms; authenticated HTTPS and OS capture agree.")
                    connected = true
                    if (networkRecoveryStarted != 0L) {
                        recoveryPhase(epoch.generation, attempt, "network_recovery", networkRecoveryStarted, "ok")
                        networkRecoveryStarted = 0L
                    }
                    if (degradedAt != 0L) {
                        Repository.log("INFO", "health.recovered",
                            "generation=${epoch.generation}; retry_elapsed=${SystemClock.elapsedRealtime() - degradedAt}ms; core retained.")
                        degradedAt = 0L
                    }
                    mutateState { it.copy(status = "Connected", active = true, busy = false, exitIp = verifiedExit, detail = config.protocol) }
                }
                Surfaces.refresh(this)
            } else {
                if (failures.failed()) {
                    // Preserve the existing two-round tolerance. Once that
                    // threshold rejects this path, try an eligible alternative
                    // immediately instead of restarting on the same interface.
                    selectUnderlay(failedEpoch = epoch)
                    checkEpoch(epoch)
                    throw IOException("Consecutive HTTPS health checks failed", lastFailure)
                }
                synchronized(control) {
                    checkEpoch(epoch)
                    degradedAt = SystemClock.elapsedRealtime()
                    update("Degraded", true, false, "HTTPS verification retry; capture and core retained")
                    Repository.log("WARN", "health.degraded",
                        "generation=${epoch.generation}; failed_rounds=${failures.count}/2; next_check=3000ms; core retained.")
                }
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
    private fun networksChanged(values: List<PhysicalNetwork>) = selectUnderlay(values)
    private fun selectUnderlay(values: List<PhysicalNetwork>? = null, failedEpoch: NetworkEpoch? = null) {
        synchronized(control) {
            if (destroyed) return
            if (values != null) physicalNetworks = values
            val before = networkEpoch
            if (failedEpoch != null && before.generation != failedEpoch.generation) return
            val previous = before.physical?.choice
            val now = SystemClock.elapsedRealtime()
            if (failedEpoch != null) previous?.let { underlaySelector.failed(it, now) }
            val selection = underlaySelector.select(physicalNetworks.map { it.choice }, previous, now, retainCurrent = desired)
            val choice = selection.choice
            val selected = physicalNetworks.firstOrNull { it.choice == choice }
            val candidates = physicalNetworks.count { !it.choice.suspended }
            val changed = previous?.generationKey != choice?.generationKey
            val epoch = NetworkEpoch(before.generation + if (changed) 1 else 0, selected)
            networkEpoch = epoch
            if (changed && desired && networkRecoveryStarted == 0L) networkRecoveryStarted = now
            val logKey = "${choice?.generationKey}/${selection.reason}"
            val reportSelection = logKey != selectionLogKey
            selectionLogKey = logKey
            val transport = when { selected == null -> "none"; choice?.wifi == true -> "wifi"; selected.capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"; else -> "other" }
            if (reportSelection && desired) Repository.log("INFO", "network.selection",
                "generation=${epoch.generation}; reason=${selection.reason}; trigger=${if (failedEpoch == null) "physical_update" else "health_threshold"}; transport=$transport; usable=$candidates; validated=${choice?.validated == true}.")
            // Health rejection and CM callbacks may race. Commit the metadata,
            // log and cancellation with this generation, before a newer cycle
            // can publish itself under the same lock.
            applyUnderlying(epoch)
            if (changed) {
                val change = when {
                    choice == null -> "lost"
                    previous == null -> "available"
                    previous.id != choice.id -> "handover"
                    previous.suspended != choice.suspended -> if (choice.suspended) "suspended" else "resumed"
                    else -> "path"
                }
                Repository.log("INFO", "network.changed",
                    "generation=${epoch.generation}; transport=$transport; change=$change; validated=${choice?.validated == true}; capture=${if (tun == null) "absent" else "retained"}; selection=${selection.reason}.")
                cancelPending()
            } else if (previous != null && choice != null && previous.validated != choice.validated) {
                Repository.log("INFO", "network.validation_changed",
                    "generation=${epoch.generation}; validated=${choice.validated}; same physical path.")
                if (choice.validated) wake.trySend(Unit)
            }
        }
    }
    private fun applyUnderlying(epoch: NetworkEpoch) {
        synchronized(control) {
            if (tun == null || !ownsSession || epoch.generation != networkEpoch.generation) return
            val networks = epoch.physical?.let { arrayOf(it.network) } ?: emptyArray()
            runCatching { check(setUnderlyingNetworks(networks)) }.onFailure { Repository.log("WARN", "network.metadata_failed", it.javaClass.simpleName) }
        }
    }
    private fun closeCore() {
        val previous = core; core = null
        if (previous != null) {
            var closed = false
            try {
                releaseCoreResources(previous::cancelPending,
                    { baseUp += previous.upload(); baseDown += previous.download() },
                    { previous.close(); closed = true })
            } finally {
                Repository.log(if (closed) "INFO" else "ERROR", if (closed) "core.stopped" else "core.stop_failed")
            }
        }
    }
    @Synchronized private fun closeCapture() {
        val previous = tun; tun = null
        val started = SystemClock.elapsedRealtime()
        val released = runCatching { previous?.close() }.onFailure { Repository.log("WARN", "capture.close_failed", it.javaClass.simpleName) }
        if (ownsSession) runCatching { setUnderlyingNetworks(null) }
        if (previous != null) {
            if (released.isSuccess) Repository.log("INFO", "capture.released")
            recoveryPhase(networkEpoch.generation, 0, "cleanup", started, if (released.isSuccess) "ok" else "failed")
        }
        released.getOrThrow()
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
    private fun owner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int): String {
        val owner = try {
            val uid = cm.getConnectionOwnerUid(protocol, InetSocketAddress(InetAddress.getByName(source), sourcePort), InetSocketAddress(InetAddress.getByName(destination), destinationPort))
            AppOwnership.resolve(uid, if (uid >= 0) packageManager.getPackagesForUid(uid)?.toList().orEmpty() else emptyList())
        } catch (error: Exception) { AppOwnership.resolve(-1, emptyList(), error.javaClass.simpleName) }
        if (appRulesEnabled) ownerNotices.next(owner, SystemClock.elapsedRealtime())?.let { (code, detail) -> Repository.log(if (owner.unresolved) "WARN" else "INFO", code, detail) }
        return JSONObject().put("uid", owner.uid).put("packages", JSONArray(owner.packages)).toString()
    }
    private fun defaultInterface(): String {
        val physical = networkEpoch.physical; val name = physical?.links?.interfaceName.orEmpty()
        return JSONObject().put("name", name).put("index", runCatching { NetworkInterface.getByName(name)?.index ?: -1 }.getOrDefault(-1)).put("expensive", physical?.choice?.metered ?: false).toString()
    }
    private fun interfaces(): String {
        val entries = JSONArray()
        for (physical in physicalNetworks) {
            val links = physical.links; val name = links.interfaceName
            val nic = runCatching { NetworkInterface.getByName(name) }.getOrNull() ?: continue
            entries.put(JSONObject().put("index", nic.index).put("name", name).put("mtu", nic.mtu).put("addresses", JSONArray(links.addresses)).put("dns", JSONArray(links.dns)).put("type", if (physical.choice.wifi) 0 else 1).put("metered", physical.choice.metered))
        }
        return entries.toString()
    }
    override fun onRevoke() {
        // Android may deliver revocation off the main thread. Serialize it with
        // command entry points and use the same generation-guarded teardown.
        scope.launch(Dispatchers.Main.immediate) {
            val state = Repository.state.value
            Repository.log("WARN", "vpn.revoked",
                "Android revoked this VPN session; generation=${networkEpoch.generation}; state=${state.status}; protocol=${state.protocol.ifEmpty { "not_started" }}.")
            stopTunnel("revoked",revoked=true)
        }
    }
    override fun onDestroy() {
        commandGeneration.incrementAndGet();destroyed = true; desired = false; cancelPending(); worker?.cancel(); ticker?.cancel()
        runCatching { cm.unregisterNetworkCallback(monitor) }
        scope.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                lifecycle.withLock {
                    withContext(NonCancellable) {
                        try { releaseSessionResources(::closeCore, ::closeCapture) }
                        catch (error: Exception) { Repository.log("ERROR", "backend.destroy_cleanup_failed", "reason=${Repository.cause(error)}; capture release attempted.") }
                        finally { update("Disconnected", false, false) }
                    }
                }
            } finally { scope.cancel() }
        }
        super.onDestroy()
    }
}
