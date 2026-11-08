@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package dev.personalvpn.client

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.enableEdgeToEdge
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.sync.withLock
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : ComponentActivity() {
    private val latency by lazy { ServerLatency(applicationContext) }
    override fun onStart() { super.onStart(); latency.start();if(backgroundGuideRequested)reviewBackground() }
    override fun onStop() { latency.stop(); super.onStop() }
    private var message by mutableStateOf<String?>(null)
    private var connectAfterNotification = false
    private var backgroundReview by mutableStateOf<BackgroundReview?>(null)
    private var backgroundGuideRequested by mutableStateOf(false)
    private var backgroundGuideAutomatic by mutableStateOf(false)
    private var confirmedBackground by mutableStateOf(emptySet<String>())
    private val backgroundPreferences by lazy { BackgroundPreferences(applicationContext) }
    private val backgroundPreferenceWrites=kotlinx.coroutines.sync.Mutex()
    private var backgroundRead: kotlinx.coroutines.Job? = null
    private val systemSettings = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { reviewBackground() }
    private val consent = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result -> if (result.resultCode == RESULT_OK) startTunnel() else message = "VPN permission was not granted. The connection was not started." }
    private val notifications = registerForActivityResult(ActivityResultContracts.RequestPermission()) { if (connectAfterNotification) { connectAfterNotification = false; consentThenConnect() } }
    private val importFiles = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        lifecycleScope.launch {
            try {
                val documents = withContext(Dispatchers.IO) { uris.map { uri -> contentResolver.openInputStream(uri)!!.use { input -> val output = java.io.ByteArrayOutputStream(); val buffer = ByteArray(4096); while (true) { val count = input.read(buffer); if (count < 0) break; require(output.size() + count <= 256 * 1024) { "Profile file is too large." }; output.write(buffer, 0, count) }; output.toString("UTF-8") } } }
                if (documents.isNotEmpty()) addProfiles(Imports.parse(documents))
            } catch (_: Exception) { message = "Could not import profile. Choose xray.json and hysteria.json, or paste the exported share links." }
        }
    }
    private val exportLogs = registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri -> if (uri != null) lifecycleScope.launch(Dispatchers.IO) { runCatching { val logs = JSONArray(Repository.logs.value.map { JSONObject().put("time", it.time).put("level", it.level).put("code", it.code).put("result", it.detail) }); contentResolver.openOutputStream(uri)?.use { it.write(logs.toString(2).toByteArray()) } }.onFailure { withContext(Dispatchers.Main) { message = "Log export failed." } } } }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        confirmedBackground=backgroundPreferences.confirmed()
        enableEdgeToEdge(statusBarStyle = SystemBarStyle.dark(0xFF0D1627.toInt()), navigationBarStyle = SystemBarStyle.dark(0xFF0D1627.toInt()))
        setContent { Client() }
        if (savedInstanceState == null) window.decorView.post { handle(intent) }
        if(savedInstanceState==null && BackgroundChecks.isLauncherOpen(intent.action,intent.categories,intent.flags))reviewBackground(fromLauncher=true)
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); handle(intent);if(BackgroundChecks.isLauncherOpen(intent.action,intent.categories,intent.flags))reviewBackground(fromLauncher=true) }
    private fun reviewBackground(show: Boolean = false,fromLauncher: Boolean = false) {
        backgroundRead?.cancel()
        backgroundRead=lifecycleScope.launch {
            val review=try {
                if(Repository.instrumentedDiagnostics)Repository.instrumentedBackgroundReview?:BackgroundChecks.read(this@MainActivity) else BackgroundChecks.read(this@MainActivity)
            } catch(cancel: kotlinx.coroutines.CancellationException) { throw cancel }
            catch(_: Exception) { BackgroundReview(BackgroundChecks.checks(BackgroundFacts()),0,null) }
            backgroundReview=review
            when {
                show -> { backgroundGuideAutomatic=false;backgroundGuideRequested=true }
                fromLauncher -> { backgroundGuideAutomatic=true;backgroundGuideRequested=review.shouldPrompt }
                backgroundGuideAutomatic && !review.shouldPrompt -> backgroundGuideRequested=false
            }
        }
    }
    private fun confirmBackground(id: String,confirmed: Boolean) {
        if(backgroundReview?.checks?.any { it.id==id && it.state==CheckState.Manual }!=true)return
        lifecycleScope.launch {
            backgroundPreferenceWrites.withLock {
                val saved=withContext(Dispatchers.IO) {
                    val current=backgroundPreferences.confirmed()
                    val next=if(confirmed)current+id else current-id
                    if(backgroundPreferences.save(next))next else null
                }
                if(saved!=null)confirmedBackground=saved else message="Could not save this choice. Try again."
            }
        }
    }
    internal fun backgroundReviewReadyForTest(): Boolean {
        check(Repository.instrumentedDiagnostics)
        return backgroundReview!=null && backgroundRead?.isCompleted==true
    }
    private fun openBackgroundSetting(target: SettingsTarget) {
        val intents=BackgroundChecks.intents(this,target,backgroundReview?.unusedStatus?:0)
        for(candidate in intents) {
            try { if(candidate.resolveActivity(packageManager)==null)continue;systemSettings.launch(candidate);return } catch(_:android.content.ActivityNotFoundException) { } catch(_:SecurityException) { }
        }
        message="This device did not expose a matching settings page. Open Android Settings and follow the displayed steps."
    }
    // Isolated instrumentation checks enlarged text without changing device settings.
    internal fun previewFontScaleForTest(scale: Float, applied: () -> Unit) {
        check(Repository.instrumentedDiagnostics)
        setContent {
            val density=androidx.compose.ui.platform.LocalDensity.current
            CompositionLocalProvider(androidx.compose.ui.platform.LocalDensity provides androidx.compose.ui.unit.Density(density.density,scale)) {
                Client()
                LaunchedEffect(scale) { withFrameNanos { }; withFrameNanos { }; applied() }
            }
        }
    }
    private fun handle(intent: Intent) {
        // The launcher Activity is exported; arbitrary other apps must not be
        // able to disconnect the VPN by fabricating a command Intent.
        if (intent.getStringExtra(Surfaces.CONTROL_TOKEN) != Repository.controlToken) return
        when (intent.action) { TunnelService.CONNECT -> connect(); TunnelService.DISCONNECT -> TunnelService.command(this, TunnelService.DISCONNECT) }
    }
    private fun connect() {
        try { EngineConfig.validate(Repository.config.value) } catch (e: Exception) { message = e.message ?: "Import a server profile first."; return }
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) { connectAfterNotification = true; notifications.launch(Manifest.permission.POST_NOTIFICATIONS) } else consentThenConnect()
    }
    private fun consentThenConnect() { val intent = VpnService.prepare(this); if (intent == null) startTunnel() else consent.launch(intent) }
    private fun startTunnel() { TunnelService.command(this, if (Repository.state.value.active) TunnelService.RECONNECT else TunnelService.CONNECT) }
    private fun save(config: Config): Boolean = try { Repository.save(config);true } catch (_: Exception) { message = "Could not save encrypted settings. Existing settings were preserved.";false }
    private fun addProfiles(profiles: List<Profile>) { val c = Repository.config.value; val first = profiles.first(); save(c.copy(profiles = c.profiles + profiles, selected = first.id, protocol = if (first.hysteria != null) "Hysteria 2" else "REALITY")) }

    @Composable private fun Client() {
        MaterialTheme(colorScheme = VpnScheme, shapes = Shapes(medium=RoundedCornerShape(18.dp),large=RoundedCornerShape(24.dp))) {
            val config by Repository.config.collectAsStateWithLifecycle()
            val state by Repository.state.collectAsStateWithLifecycle()
            val latencyResults by latency.results.collectAsStateWithLifecycle()
            LaunchedEffect(config.profiles,config.protocol) { latency.profiles(config.profiles,config.protocol) }
            val dirty = remember(config, state.active, state.appliedConfig) { state.active && config.fingerprint() != state.appliedConfig }
            var page by remember { mutableStateOf("Home") }
            var latencyDetail by remember { mutableStateOf<String?>(null) }
            var pasted by remember { mutableStateOf(false) }
            var editing by remember { mutableStateOf<Profile?>(null) }
            var confirmProtocol by remember { mutableStateOf<String?>(null) }
            BackHandler(page != "Home") { page = if (page == "Profiles") "Home" else "Settings" }
            Surface(Modifier.fillMaxSize(),color=Color.Transparent,contentColor=MaterialTheme.colorScheme.onSurface) {
                Box(Modifier.fillMaxSize().background(VpnBackground).systemBarsPadding().imePadding(), contentAlignment = Alignment.TopCenter) {
                    Column(Modifier.fillMaxHeight().widthIn(max = 560.dp).fillMaxWidth().padding(horizontal = 18.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Row(Modifier.fillMaxWidth().heightIn(min=48.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.width(70.dp)) { if (page !in listOf("Home", "Settings")) TextButton(onClick = { page = if (page == "Profiles") "Home" else "Settings" }) { Text("‹ Back") } }
                            Text(page, Modifier.weight(1f), fontSize = 23.sp, fontWeight = FontWeight.SemiBold, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                            Spacer(Modifier.width(70.dp))
                        }
                        Box(Modifier.weight(1f)) {
                            when (page) {
                                "Home" -> Home(config, state, dirty, latencyResults, { target -> latency.refresh(only=target) }, { page = "Profiles" }, { protocol -> if (state.active) confirmProtocol = protocol else save(config.copy(protocol = protocol)) }, {
                                    when(connectionAction(state,dirty)) {
                                        ConnectionAction.Cancel,ConnectionAction.Disconnect -> TunnelService.command(this@MainActivity,TunnelService.DISCONNECT)
                                        ConnectionAction.Connect,ConnectionAction.Apply -> connect()
                                        ConnectionAction.Stopping -> Unit
                                    }
                                },managedSettings={openBackgroundSetting(SettingsTarget.Vpn)})
                                "Settings" -> Column(Modifier.verticalScroll(rememberScrollState()),verticalArrangement = Arrangement.spacedBy(10.dp)) {
                                    Text("CONNECTION & ROUTING",fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
                                    SettingsRow("VPN configure", "Server, credentials and protocol settings") { page = "Profiles" }
                                    SettingsRow("App Tunnel", "Installed app rules · App exceptions win") { page = "App Tunnel" }
                                    SettingsRow("Site Tunnel", "Domain, IP and CIDR rules") { page = "Site Tunnel" }
                                    SettingsRow("Backend logs", "Connection and recovery events") { page = "Backend logs" }
                                    SettingsRow("Background reliability", "Battery, background access and last process exit") { reviewBackground(show=true) }
                                    SettingsRow("Android VPN settings", "Always-on and block connections without VPN") { openBackgroundSetting(SettingsTarget.Vpn) }
                                    if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) SettingsRow("Enable notifications", "Required to see current speed in the notification drawer") { notifications.launch(Manifest.permission.POST_NOTIFICATIONS) }
                                    Row(verticalAlignment = Alignment.CenterVertically) { Text("IPv6 DNS", Modifier.weight(1f)); Switch(config.ipv6Dns, { save(config.copy(ipv6Dns = it)) }) }
                                    Text("Enable IPv6 DNS only with working VPS IPv6.\nAndroid allows one VPN per user/profile.\nPersonal VPN ${BuildConfig.VERSION_NAME}", fontSize = 12.sp, color = Color(0xFF9CAEC8))
                                }
                                "Profiles" -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                        OutlinedButton(onClick = { importFiles.launch(arrayOf("application/json", "text/plain", "application/octet-stream")) }) { Text("Import files") }
                                        OutlinedButton(onClick = { pasted = true }) { Text("Paste links") }
                                        TextButton(onClick = { editing = Profile() }) { Text("Add") }
                                    }
                                    TextButton(onClick={latency.refresh()},modifier=Modifier.fillMaxWidth()) { Text("Refresh latency")
                                    }
                                    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) { items(config.profiles, key = { it.id }) { profile ->
                                        Card(onClick = { save(config.copy(selected = profile.id)) }, modifier = Modifier.fillMaxWidth()) {
                                            Column(Modifier.padding(14.dp)) {
                                                Row(Modifier.fillMaxWidth(),verticalAlignment=Alignment.CenterVertically) {
                                                    Text("${flag(profile.country)}  ${profile.name}${if(profile.id==config.selected) "  ✓" else ""}",Modifier.weight(1f),fontWeight=FontWeight.Medium,maxLines=2)
                                                    val measured=profile.latencyTarget(config.protocol)?.let { latencyResults[it.key] }?:LatencyResult()
                                                    TextButton(onClick={latencyDetail=measured.detail},modifier=Modifier.heightIn(min=48.dp)) { Text(measured.label,fontSize=12.sp,color=MaterialTheme.colorScheme.secondary) }
                                                }
                                                Row { TextButton(onClick = { editing = profile }) { Text("Configure") }; TextButton(onClick = { if (profile.id == config.selected && state.active) message = "Disconnect before deleting the active server." else save(config.copy(profiles = config.profiles.filterNot { it.id == profile.id }, selected = config.selected.takeUnless { it == profile.id })) }) { Text("Delete") } }
                                            }
                                        }
                                    } }
                                }
                                "App Tunnel" -> AppTunnelPage(config.apps,state.active) { save(config.copy(apps = it)) }
                                "Site Tunnel" -> SiteTunnelPage(config.sites) { save(config.copy(sites = it)) }
                                "Backend logs" -> Logs { exportLogs.launch("personal-vpn-session.json") }
                            }
                        }
                        if(page in listOf("Home","Settings")) Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) { for (tab in listOf("Home", "Settings")) Button(onClick = { page = tab }, modifier = Modifier.weight(1f), colors = ButtonDefaults.buttonColors(containerColor = if (page == tab) Color(0xFF225367) else Color(0xFF19283B), contentColor = Color.White)) { Text(tab) } }
                    }
                }
            }
            latencyDetail?.let { text -> VpnSheet("Server latency",{latencyDetail=null}) { Text(text) } }
            message?.let { text -> AlertDialog(onDismissRequest = { message = null }, title = { Text("Personal VPN") }, text = { Text(text) }, confirmButton = { TextButton(onClick = { message = null }) { Text("OK") } }) }
            confirmProtocol?.let { protocol -> VpnSheet("Switch to $protocol?",{confirmProtocol=null}) { Text("The current connection will reconnect with the selected protocol.");Button(onClick={save(config.copy(protocol=protocol));confirmProtocol=null;startTunnel()},modifier=Modifier.fillMaxWidth()) { Text("Confirm") } } }
            if (pasted) PasteDialog({ pasted = false }) { text -> try { addProfiles(Imports.parse(listOf(text))); pasted = false } catch (e: Exception) { message = e.message ?: "Invalid profile." } }
            editing?.let { profile -> ProfileDialog(profile, { editing = null }) { changed -> val current = Repository.config.value; save(current.copy(profiles = current.profiles.filterNot { it.id == changed.id } + changed, selected = current.selected ?: changed.id)); editing = null } }
            if(backgroundGuideRequested && !state.busy)backgroundReview?.let { review ->
                BackgroundGuidance(review,backgroundGuideAutomatic,confirmedBackground,{backgroundGuideRequested=false},{reviewBackground()},::openBackgroundSetting,::confirmBackground)
            }
        }
    }


}

@Composable private fun Home(config: Config,state: TunnelState,dirty: Boolean,latencies: Map<String,LatencyResult>,refresh: (LatencyTarget)->Unit,profiles: ()->Unit,protocol: (String)->Unit,connect: ()->Unit,managedSettings: ()->Unit) {
    var expanded by remember { mutableStateOf(false) }
    var latencyInfo by remember { mutableStateOf(false) }
    val profile=config.profiles.find { it.id==config.selected }
    val target=profile?.latencyTarget(config.protocol);val measured=target?.let { latencies[it.key] }?:LatencyResult()
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val compact=maxHeight<500.dp || androidx.compose.ui.platform.LocalDensity.current.fontScale>1.3f
        Column((if(compact)Modifier.fillMaxSize().verticalScroll(rememberScrollState()) else Modifier.fillMaxSize()),horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(8.dp)) {
            Card(onClick=profiles,modifier=Modifier.fillMaxWidth(),elevation=CardDefaults.cardElevation(defaultElevation=2.dp),colors=CardDefaults.cardColors(containerColor=Color(0xFF233952)),border=androidx.compose.foundation.BorderStroke(1.dp,Color(0xFF3A506B))) {
                Row(Modifier.fillMaxWidth().padding(14.dp),verticalAlignment=Alignment.CenterVertically) {
                    Text(profile?.let { flag(it.country) }?:"◎",fontSize=28.sp);Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) { Text(profile?.name?:"Add a server profile",maxLines=1,overflow=androidx.compose.ui.text.style.TextOverflow.Ellipsis);Text("Server profiles",fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant) }
                    TextButton(onClick={latencyInfo=true},enabled=target!=null,modifier=Modifier.heightIn(min=48.dp)) { Text(measured.label,fontSize=12.sp) };Text("›",fontSize=24.sp)
                }
            }
            OutlinedButton(onClick={expanded=true},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).semantics { contentDescription="Choose VPN protocol" }) { Text(config.protocol,Modifier.weight(1f));Text("⌄") }
            Text(state.status,fontSize=23.sp,color=when { state.status=="Connected"->MaterialTheme.colorScheme.primary;state.active->Color(0xFFAFD5FF);else->MaterialTheme.colorScheme.onSurface })
            Box(if(compact)Modifier.fillMaxWidth().height(166.dp)else Modifier.weight(1f).fillMaxWidth()) { PowerButton(state,dirty,connect) }
            Text("Exit IP: ${state.exitIp}",color=MaterialTheme.colorScheme.primary,fontSize=13.sp)
            Text(if(dirty) "Settings changed · reconnect to apply" else state.detail.ifBlank { "Android TUN" },fontSize=11.sp,maxLines=2,color=MaterialTheme.colorScheme.onSurfaceVariant)
            if(state.detail.startsWith("Always-on"))OutlinedButton(onClick=managedSettings,modifier=Modifier.heightIn(min=48.dp)) { Text("Open Android VPN settings") }
            Card(Modifier.fillMaxWidth(),elevation=CardDefaults.cardElevation(defaultElevation=2.dp),border=androidx.compose.foundation.BorderStroke(1.dp,Color(0xFF30465F))) {
                Column(Modifier.padding(horizontal=14.dp,vertical=10.dp),verticalArrangement=Arrangement.spacedBy(4.dp)) {
                    Text("TUNNEL TRAFFIC",fontSize=10.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
                    Row(Modifier.fillMaxWidth()) { Column(Modifier.weight(1f)) { Text("↑ ${bytes(state.uploadRate)}/s",color=Color(0xFF32D4B2),fontSize=18.sp);Text("${bytes(state.uploaded)} sent",fontSize=10.sp) };Column(Modifier.weight(1f)) { Text("↓ ${bytes(state.downloadRate)}/s",color=Color(0xFF80B4FA),fontSize=18.sp);Text("${bytes(state.downloaded)} received",fontSize=10.sp) } }
                    TrafficGraph(state.history)
                }
            }
        }
    }
    if(expanded) VpnSheet("VPN protocol",{expanded=false}) { listOf("REALITY","Hysteria 2").forEach { name -> OutlinedButton(onClick={expanded=false;protocol(name)},enabled=if(name=="REALITY")profile?.reality!=null else profile?.hysteria!=null,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Column(Modifier.fillMaxWidth(),horizontalAlignment=Alignment.Start) { Text(name,Modifier.fillMaxWidth(),textAlign=androidx.compose.ui.text.style.TextAlign.Start);Text(if(name=="REALITY") "TLS over TCP · streams reconnect on handover" else "QUIC over UDP · requires working UDP",Modifier.fillMaxWidth(),fontSize=11.sp,textAlign=androidx.compose.ui.text.style.TextAlign.Start) } } } }
    if(latencyInfo) VpnSheet("Server latency",{latencyInfo=false}) { Text(measured.detail);Button(onClick={target?.let(refresh);latencyInfo=false},enabled=target!=null,modifier=Modifier.fillMaxWidth()) { Text("Refresh latency") } }
}
@Composable private fun SettingsRow(title: String,detail: String,click: ()->Unit) {
    Card(onClick=click,modifier=Modifier.fillMaxWidth(),elevation=CardDefaults.cardElevation(defaultElevation=2.dp),border=androidx.compose.foundation.BorderStroke(1.dp,Color(0xFF30465F))) {
        Row(Modifier.fillMaxWidth().heightIn(min=72.dp).padding(12.dp),verticalAlignment=Alignment.CenterVertically,horizontalArrangement=Arrangement.spacedBy(12.dp)) {
            MenuIcon(title);Column(Modifier.weight(1f)) { Text(title,fontSize=16.sp);if(detail.isNotEmpty())Text(detail,fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant) };Text("›",fontSize=24.sp)
        }
    }
}
@Composable private fun Logs(export: () -> Unit) {
    val logs by Repository.logs.collectAsStateWithLifecycle(); val formatter = remember { SimpleDateFormat("HH:mm:ss", Locale.US) }
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) { Row { OutlinedButton(onClick = { Repository.clearLogs() }) { Text("Clear") }; Spacer(Modifier.width(10.dp)); OutlinedButton(onClick = export) { Text("Export") } }; LazyColumn(Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) { items(logs) { event -> Column { Text("${formatter.format(Date(event.time))}  ${event.level}  ${event.code}", fontSize = 12.sp, color = if (event.level == "ERROR") Color(0xFFFFBA9B) else Color(0xFF8FDACF)); if (event.detail.isNotEmpty()) Text(event.detail, fontSize = 12.sp) } } } }
}
@Composable private fun PasteDialog(dismiss: ()->Unit,add: (String)->Unit) { var text by remember { mutableStateOf("") };VpnSheet("Import profile",dismiss) { OutlinedTextField(text,{text=it},label={Text("VLESS / Hysteria links or export JSON")},modifier=Modifier.fillMaxWidth().height(200.dp));Button(onClick={add(text)},enabled=text.isNotBlank(),modifier=Modifier.fillMaxWidth()) { Text("Import") } } }
@Composable private fun ProfileDialog(profile: Profile, dismiss: () -> Unit, save: (Profile) -> Unit) {
    var name by remember { mutableStateOf(profile.name) }; var country by remember { mutableStateOf(profile.country) }; var protocol by remember { mutableStateOf(if (profile.hysteria != null) "Hysteria 2" else "REALITY") }
    var reality by remember { mutableStateOf(profile.reality ?: "{}") }; var hysteria by remember { mutableStateOf(profile.hysteria ?: "{}") }; var error by remember { mutableStateOf("") }
    val current = JSONObject(if (protocol == "REALITY") reality else hysteria)
    fun set(field: String, value: String) { current.put(field, if (field == "port") value.toIntOrNull() ?: 0 else value); if (protocol == "REALITY") reality = current.toString() else hysteria = current.toString() }
    VpnSheet("VPN configure",dismiss) {
        OutlinedTextField(name, { name = it }, label = { Text("Profile name") }, singleLine = true)
        OutlinedTextField(country, { country = it.uppercase().take(2) }, label = { Text("Country code, e.g. JP") }, singleLine = true)
        Row { listOf("REALITY", "Hysteria 2").forEach { p -> FilterChip(protocol == p, { protocol = p }, label = { Text(p) }) } }
        Text(if (protocol == "REALITY") "TCP / REALITY · streams may reconnect after a network change." else "QUIC / UDP · may fail on networks that block UDP.", fontSize = 12.sp, color = Color(0xFF9CAEC8))
        val fields = if (protocol == "REALITY") listOf("server" to "Server IP / hostname", "port" to "Port", "uuid" to "UUID", "sni" to "TLS server name", "publicKey" to "REALITY public key", "shortId" to "Short ID") else listOf("server" to "Server IP / hostname", "port" to "Port", "auth" to "Authentication", "sni" to "TLS server name", "pin" to "Certificate SHA-256 pin", "obfs" to "Salamander password (optional)")
        for ((key, label) in fields) OutlinedTextField(current.optString(key, if (key == "port") "443" else ""), { set(key, it) }, label = { Text(label) }, singleLine = true, visualTransformation = if (key in listOf("auth", "obfs", "uuid")) PasswordVisualTransformation() else VisualTransformation.None)
        if (error.isNotEmpty()) Text(error, color = Color(0xFFFFBA9B), fontSize = 12.sp)
    Button(modifier=Modifier.fillMaxWidth().heightIn(min=48.dp),onClick = { try { if (!current.has("port")) set("port", "443"); if (protocol == "Hysteria 2") { if (!current.has("pin")) set("pin", ""); if (!current.has("obfs")) set("obfs", "") }; if (protocol == "REALITY" && !current.has("shortId")) set("shortId", ""); Imports.validate(protocol, current); require(name.isNotBlank() && country.matches(Regex("[A-Z]{2}"))); save(profile.copy(name = name, country = country, reality = reality.takeIf { it != "{}" }, hysteria = hysteria.takeIf { it != "{}" })) } catch (e: Exception) { error = e.message ?: "Invalid profile." } }) { Text("Save") }
    }
}
private fun flag(country: String) = if (country.length == 2 && country.all { it in 'A'..'Z' }) country.map { String(Character.toChars(0x1F1E6 + it.code - 'A'.code)) }.joinToString("") else "◎"
internal fun modeLabel(action: Action) = if (action == Action.Disabled) "Rules off" else "${action.name} by default"
