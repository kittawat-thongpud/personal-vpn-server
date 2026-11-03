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
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import javax.net.ssl.HttpsURLConnection

class TunnelService : VpnService() {
    companion object {
        const val CONNECT = "dev.personalvpn.CONNECT"
        const val DISCONNECT = "dev.personalvpn.DISCONNECT"
        const val RECONNECT = "dev.personalvpn.RECONNECT"
        fun command(context: android.content.Context, action: String) { ContextCompat.startForegroundService(context, Intent(context, TunnelService::class.java).setAction(action)) }
    }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val lifecycle = Mutex()
    private var worker: Job? = null
    private var ticker: Job? = null
    @Volatile private var core: Core? = null
    @Volatile private var tun: ParcelFileDescriptor? = null
    @Volatile private var network: Network? = null
    @Volatile private var strictApps = false
    @Volatile private var desired = false
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val cm by lazy { getSystemService(ConnectivityManager::class.java) }
    @Volatile private var networkSignature = ""
    private val platform = object : Platform {
        override fun openTun(mtu: Int) = this@TunnelService.openTun(mtu)
        override fun protect(fd: Int) = protectSocket(fd)
        override fun owner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int) = this@TunnelService.owner(protocol, source, sourcePort, destination, destinationPort)
        override fun interfaces() = this@TunnelService.interfaces()
        override fun defaultInterface() = this@TunnelService.defaultInterface()
    }
    private var baseUp = 0L; private var baseDown = 0L
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = selectNetwork()
        override fun onLost(network: Network) = selectNetwork()
        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) = selectNetwork()
        override fun onLinkPropertiesChanged(network: Network, linkProperties: LinkProperties) = selectNetwork()
    }
    override fun onCreate() {
        super.onCreate(); Surfaces.channels(this)
        cm.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(), callback)
        selectNetwork()
    }
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (prepare(this) != null) { Repository.log("WARN", "vpn.consent_required"); stopSelf(); return START_NOT_STICKY }
        val notification = Surfaces.notification(this)
        if (Build.VERSION.SDK_INT >= 34) startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SYSTEM_EXEMPTED) else startForeground(1, notification)
        when (intent?.action) {
            DISCONNECT -> {
                if (isAlwaysOn) { Repository.log("WARN", "disconnect.managed", "Always-on is managed in Android VPN settings."); Repository.state.update { it.copy(detail = "Always-on is managed in Android VPN settings.") }; Surfaces.refresh(this); return START_STICKY }
                desired = false; worker?.cancel(); worker = scope.launch { lifecycle.withLock { closeCore(); closeCapture(); update("Disconnected", false, false); withContext(Dispatchers.Main) { stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() } } }
            }
            else -> {
                if (desired && intent?.action != RECONNECT) return START_STICKY
                if (!desired && tun == null) {
                    baseUp = 0; baseDown = 0
                    Repository.state.update { it.copy(exitIp = "—", appliedConfig = "", uploadRate = 0, downloadRate = 0, uploaded = 0, downloaded = 0, history = emptyList()) }
                }
                desired = true; worker?.cancel()
                worker = scope.launch { lifecycle.withLock { runSession(Repository.config.value) } }
                if (ticker == null) ticker = scope.launch { streamTraffic() }
            }
        }
        return if (desired) START_STICKY else START_NOT_STICKY
    }
    private suspend fun runSession(config: Config) {
        var attempt = 0; var nextHealth = 0L; var appliedNetwork = ""
        strictApps = config.apps.mode != Action.Disabled
        try {
            EngineConfig.validate(config)
            while (currentCoroutineContext().isActive && desired) {
                var phase = "network"
                try {
                    val physical = network
                    if (physical == null) { update("Waiting for network", true, true); withTimeoutOrNull(3000) { wake.receive() }; continue }
                    if (core == null) {
                        update(if (attempt == 0) "Connecting" else "Reconnecting", true, true)
                        val host = config.selectedProfile().endpoint(config.protocol)
                        val resolved = physical.getAllByName(host).firstOrNull { it is java.net.Inet4Address } ?: physical.getAllByName(host).first()
                        val content = EngineConfig.compile(config, resolved.hostAddress!!, Repository.instrumentedCertificate != null)
                        phase = "core_start"
                        Personalvpn.check(content)
                        core = Personalvpn.start(noBackupFilesDir.absolutePath, content, platform)
                        currentCoroutineContext().ensureActive()
                        Repository.state.update { it.copy(appliedConfig = config.fingerprint(), profileName = config.selectedProfile().name, protocol = config.protocol) }
                        Repository.log("INFO", "core.started", config.protocol)
                        appliedNetwork = networkSignature; nextHealth = 0L
                    }
                    if (appliedNetwork != networkSignature) {
                        update("Reconnecting", true, true, "Underlying network changed")
                        core?.networkChanged(); appliedNetwork = networkSignature; nextHealth = 0L
                        Repository.log("INFO", "network.handover", "Protected transport updated; TUN retained.")
                    }
                    if (SystemClock.elapsedRealtime() >= nextHealth) {
                        val wasConnected = Repository.state.value.status == "Connected"
                        if (!wasConnected) update("Verifying", true, true)
                        val certificate = Repository.instrumentedCertificate
                        phase = "outbound_https"
                        val verified = if (certificate != null) core!!.verifyAt("https://fixture.local:18445/ip", certificate) else core!!.verify()
                        if (Repository.instrumentedDiagnostics) Repository.log("INFO", "fixture.outbound_verified", verified)
                        currentCoroutineContext().ensureActive()
                        // Separate OS-capture proof: this app is captured and its
                        // reserved rule routes health traffic through the VPN.
                        phase = "captured_https"
                        val captured = capturedExit()
                        currentCoroutineContext().ensureActive()
                        phase = "exit_compare"
                        check(verified == captured) { "Captured exit differs from authenticated outbound." }
                        if (!wasConnected) Repository.log("INFO", "health.verified", "Authenticated HTTPS and captured exit agree.")
                        Repository.state.update { it.copy(status = "Connected", active = true, busy = false, exitIp = verified, detail = config.protocol) }
                        Surfaces.refresh(this); attempt = 0; nextHealth = SystemClock.elapsedRealtime() + 15000
                    }
                    withTimeoutOrNull(1000) { wake.receive() }
                } catch (cancel: CancellationException) { throw cancel }
                catch (error: Exception) {
                    attempt++
                    val failedNetwork = networkSignature
                    val reason = Repository.cause(error)
                    val delayMs = minOf(30000L, 1000L shl minOf(attempt, 5)) + (0..400).random()
                    Repository.log("WARN", "backend.retry", "Attempt $attempt; stage=$phase; reason=$reason; retry in ${delayMs}ms; no Direct fallback.")
                    update("Reconnecting", true, true, "$phase: $reason")
                    closeCore()
                    // Keep the original TUN FD: a transport failure must not
                    // deliberately return captured application traffic to Direct.
                    awaitNetworkChange(wake, delayMs) { networkSignature != failedNetwork }
                }
            }
        } catch (cancel: CancellationException) { throw cancel }
        catch (error: Exception) { Repository.log("ERROR", "backend.failed", Repository.cause(error)); update("Error", tun != null, false, "Check profile and backend logs.") }
        finally { closeCore() }
    }
    private fun capturedExit(): String {
        val certificate = Repository.instrumentedCertificate
        val connection = java.net.URL(if (certificate != null) "https://fixture.local:18445/ip" else "https://api.ipify.org/").openConnection(java.net.Proxy.NO_PROXY) as HttpsURLConnection
        if (certificate != null) connection.sslSocketFactory = fixtureTrust(certificate)
        connection.connectTimeout = 8000; connection.readTimeout = 8000; connection.useCaches = false
        return try { check(connection.responseCode == 200); connection.inputStream.use { input -> val out = java.io.ByteArrayOutputStream(); val buffer = ByteArray(128); while (true) { val count = input.read(buffer); if (count < 0) break; check(out.size() + count <= 128); out.write(buffer, 0, count) }; out.toString("UTF-8").trim() } } finally { connection.disconnect() }
    }
    private fun closeCore() { val previous = core; core = null; if (previous != null) { baseUp += previous.upload(); baseDown += previous.download(); previous.close(); Repository.log("INFO", "core.stopped") } }
    private fun closeCapture() { val previous = tun; tun = null; runCatching { previous?.close() }.onFailure { Repository.log("WARN", "capture.close_failed", it.javaClass.simpleName) }; runCatching { setUnderlyingNetworks(null) }; if (previous != null) Repository.log("INFO", "capture.released") }
    private fun update(status: String, active: Boolean, busy: Boolean, detail: String = "") {
        if (status != Repository.state.value.status) Repository.log("INFO", "state.${status.lowercase().replace(' ', '_')}", detail)
        Repository.state.update { it.copy(status = status, active = active, busy = busy, detail = detail, exitIp = if (active) it.exitIp else "—", uploadRate = if (active) it.uploadRate else 0, downloadRate = if (active) it.downloadRate else 0) }
        Surfaces.refresh(this)
    }
    private suspend fun streamTraffic() {
        var lastUp = 0L; var lastDown = 0L; var lastTime = SystemClock.elapsedRealtime()
        while (currentCoroutineContext().isActive) {
            delay(1000)
            val current = core
            val up = baseUp + (current?.upload() ?: 0); val down = baseDown + (current?.download() ?: 0)
            val now = SystemClock.elapsedRealtime(); val elapsed = maxOf(1, now - lastTime)
            val upRate = maxOf(0, up - lastUp) * 1000 / elapsed; val downRate = maxOf(0, down - lastDown) * 1000 / elapsed
            Repository.state.update { old -> old.copy(uploadRate = if (old.active) upRate else 0, downloadRate = if (old.active) downRate else 0, uploaded = maxOf(old.uploaded, up), downloaded = maxOf(old.downloaded, down), history = (old.history + (upRate to downRate)).takeLast(60)) }
            if (desired) Surfaces.rate(this)
            lastUp = up; lastDown = down; lastTime = now
        }
    }
    @Synchronized private fun selectNetwork() {
        val candidates = cm.allNetworks.filter { val c = cm.getNetworkCapabilities(it); c?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) == true && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) }
        val selected = candidates.maxByOrNull { val c = cm.getNetworkCapabilities(it)!!; (if (c.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) 10 else 0) + (if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) 2 else 1) }
        val links = selected?.let { cm.getLinkProperties(it) }
        val signature = "$selected/${links?.interfaceName}/${links?.dnsServers}"
        network = selected
        if (selected != null) setUnderlyingNetworks(arrayOf(selected))
        if (signature != networkSignature) { networkSignature = signature; wake.trySend(Unit) }
    }
    @Synchronized private fun openTun(mtu: Int): Int {
        tun?.let { return it.fd }
        val builder = Builder().setSession("Personal VPN").setMtu(mtu).addAddress("172.29.255.1", 30).addAddress("fd66:706e:1::1", 126).addRoute("0.0.0.0", 0).addRoute("::", 0).addDnsServer("172.29.255.2").setBlocking(false).setMetered(false)
        builder.setConfigureIntent(android.app.PendingIntent.getActivity(this, 10, Intent(this, MainActivity::class.java), android.app.PendingIntent.FLAG_UPDATE_CURRENT or android.app.PendingIntent.FLAG_IMMUTABLE))
        tun = builder.establish() ?: error("Android VPN consent was revoked.")
        Repository.log("INFO", "capture.established", "IPv4 and IPv6 capture; all app rules run inside the core.")
        return tun!!.fd
    }
    private fun protectSocket(fd: Int) {
        check(super.protect(fd)) { "Socket protection failed." }
        val underlying = network ?: error("Underlying network unavailable.")
        ParcelFileDescriptor.fromFd(fd).use { underlying.bindSocket(it.fileDescriptor) }
    }
    private fun owner(protocol: Int, source: String, sourcePort: Int, destination: String, destinationPort: Int): String {
        return try {
            val uid = cm.getConnectionOwnerUid(protocol, InetSocketAddress(InetAddress.getByName(source), sourcePort), InetSocketAddress(InetAddress.getByName(destination), destinationPort))
            val packages = packageManager.getPackagesForUid(uid)?.toList().orEmpty()
            val safe = if (uid < 0 || (strictApps && packages.size != 1)) listOf("__unresolved__") else packages
            JSONObject().put("uid", uid).put("packages", JSONArray(safe)).toString()
        } catch (_: Exception) { "{\"uid\":-1,\"packages\":[\"__unresolved__\"]}" }
    }
    private fun defaultInterface(): String {
        val n = network; val links = n?.let { cm.getLinkProperties(it) }; val name = links?.interfaceName.orEmpty()
        return JSONObject().put("name", name).put("index", runCatching { NetworkInterface.getByName(name)?.index ?: -1 }.getOrDefault(-1)).put("expensive", n?.let { cm.getNetworkCapabilities(it)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == false } ?: false).toString()
    }
    private fun interfaces(): String {
        val entries = JSONArray()
        for (n in cm.allNetworks) {
            val c = cm.getNetworkCapabilities(n) ?: continue
            if (!c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)) continue
            val links = cm.getLinkProperties(n) ?: continue; val name = links.interfaceName ?: continue
            val nic = NetworkInterface.getByName(name) ?: continue
            entries.put(JSONObject().put("index", nic.index).put("name", name).put("mtu", nic.mtu).put("addresses", JSONArray(links.linkAddresses.map { it.toString() })).put("dns", JSONArray(links.dnsServers.map { it.hostAddress })).put("type", if (c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) 0 else 1).put("metered", !c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)))
        }
        return entries.toString()
    }
    override fun onRevoke() { desired = false; Repository.log("WARN", "vpn.revoked", "Android revoked this VPN session."); worker?.cancel(); scope.launch { lifecycle.withLock { closeCore(); closeCapture(); update("Disconnected", false, false); stopSelf() } } }
    override fun onDestroy() {
        desired = false; worker?.cancel(); ticker?.cancel(); runCatching { cm.unregisterNetworkCallback(callback) }
        // Blocking native work has deadlines; serialize final resource closure.
        scope.launch { lifecycle.withLock { closeCore(); closeCapture(); update("Disconnected", false, false) }; scope.cancel() }
        super.onDestroy()
    }
}
