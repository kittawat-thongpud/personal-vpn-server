package dev.personalvpn.client

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
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
import org.json.JSONArray
import org.json.JSONObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

class MainActivity : ComponentActivity() {
    private var message by mutableStateOf<String?>(null)
    private var dirty by mutableStateOf(false)
    private var connectAfterNotification = false
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
        setContent { Client() }
        if (savedInstanceState == null) window.decorView.post { handle(intent) }
    }
    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); handle(intent) }
    private fun handle(intent: Intent) { when (intent.action) { TunnelService.CONNECT -> connect(); TunnelService.DISCONNECT -> TunnelService.command(this, TunnelService.DISCONNECT) } }
    private fun connect() {
        try { EngineConfig.validate(Repository.config.value) } catch (e: Exception) { message = e.message ?: "Import a server profile first."; return }
        if (Build.VERSION.SDK_INT >= 33 && ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) { connectAfterNotification = true; notifications.launch(Manifest.permission.POST_NOTIFICATIONS) } else consentThenConnect()
    }
    private fun consentThenConnect() { val intent = VpnService.prepare(this); if (intent == null) startTunnel() else consent.launch(intent) }
    private fun startTunnel() { TunnelService.command(this, if (dirty && Repository.state.value.active) TunnelService.RECONNECT else TunnelService.CONNECT); dirty = false }
    private fun save(config: Config) { try { Repository.save(config); dirty = Repository.state.value.active } catch (_: Exception) { message = "Could not save encrypted settings. Existing settings were preserved." } }
    private fun addProfiles(profiles: List<Profile>) { val c = Repository.config.value; save(c.copy(profiles = c.profiles + profiles, selected = profiles.first().id)) }

    @Composable private fun Client() {
        val scheme = darkColorScheme(primary = Color(0xFF13C6A2), onPrimary = Color(0xFF06272D), background = Color(0xFF0D1627), surface = Color(0xFF19283B), onSurface = Color(0xFFECF2FF), secondary = Color(0xFF5C9FFF))
        MaterialTheme(colorScheme = scheme) {
            val config by Repository.config.collectAsStateWithLifecycle()
            val state by Repository.state.collectAsStateWithLifecycle()
            var page by remember { mutableStateOf("Home") }
            var pasted by remember { mutableStateOf(false) }
            var editing by remember { mutableStateOf<Profile?>(null) }
            var confirmProtocol by remember { mutableStateOf<String?>(null) }
            BackHandler(page != "Home") { page = if (page == "Profiles") "Home" else "Settings" }
            Surface(Modifier.fillMaxSize()) {
                Box(Modifier.fillMaxSize().systemBarsPadding(), contentAlignment = Alignment.TopCenter) {
                    Column(Modifier.fillMaxHeight().widthIn(max = 560.dp).fillMaxWidth().padding(horizontal = 18.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Row(Modifier.fillMaxWidth().height(42.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.width(70.dp)) { if (page !in listOf("Home", "Settings")) TextButton(onClick = { page = if (page == "Profiles") "Home" else "Settings" }) { Text("‹ Back") } }
                            Text(page, Modifier.weight(1f), fontSize = 23.sp, fontWeight = FontWeight.SemiBold, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                            Spacer(Modifier.width(70.dp))
                        }
                        Box(Modifier.weight(1f)) {
                            when (page) {
                                "Home" -> Home(config, state, dirty, { page = "Profiles" }, { protocol -> if (state.active) confirmProtocol = protocol else save(config.copy(protocol = protocol)) }, { if (state.active && !dirty) TunnelService.command(this@MainActivity, TunnelService.DISCONNECT) else connect() })
                                "Settings" -> Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                                    SettingsRow("VPN configure", "Server, credentials and protocol settings") { page = "Profiles" }
                                    SettingsRow("App Tunnel", "Installed app rules · App exceptions win") { page = "App Tunnel" }
                                    SettingsRow("Site Tunnel", "Domain, IP and CIDR rules") { page = "Site Tunnel" }
                                    SettingsRow("Backend logs", "") { page = "Backend logs" }
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
                                    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) { items(config.profiles, key = { it.id }) { profile ->
                                        Card(onClick = { save(config.copy(selected = profile.id)) }, modifier = Modifier.fillMaxWidth()) {
                                            Column(Modifier.padding(14.dp)) {
                                                Text("${flag(profile.country)}  ${profile.name}${if (profile.id == config.selected) "  ✓" else ""}", fontWeight = FontWeight.Medium)
                                                Row { TextButton(onClick = { editing = profile }) { Text("Configure") }; TextButton(onClick = { if (profile.id == config.selected && state.active) message = "Disconnect before deleting the active server." else save(config.copy(profiles = config.profiles.filterNot { it.id == profile.id }, selected = config.selected.takeUnless { it == profile.id })) }) { Text("Delete") } }
                                            }
                                        }
                                    } }
                                }
                                "App Tunnel" -> RulesPage(true, config.apps) { save(config.copy(apps = it)) }
                                "Site Tunnel" -> RulesPage(false, config.sites) { save(config.copy(sites = it)) }
                                "Backend logs" -> Logs { exportLogs.launch("personal-vpn-session.json") }
                            }
                        }
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) { for (tab in listOf("Home", "Settings")) Button(onClick = { page = tab }, modifier = Modifier.weight(1f), colors = ButtonDefaults.buttonColors(containerColor = if (page == tab) Color(0xFF225367) else Color(0xFF19283B), contentColor = Color.White)) { Text(tab) } }
                    }
                }
            }
            message?.let { text -> AlertDialog(onDismissRequest = { message = null }, title = { Text("Personal VPN") }, text = { Text(text) }, confirmButton = { TextButton(onClick = { message = null }) { Text("OK") } }) }
            confirmProtocol?.let { protocol -> AlertDialog(onDismissRequest = { confirmProtocol = null }, title = { Text("Switch to $protocol?") }, text = { Text("The current connection will reconnect with the selected protocol.") }, confirmButton = { TextButton(onClick = { save(config.copy(protocol = protocol)); confirmProtocol = null; startTunnel() }) { Text("Confirm") } }, dismissButton = { TextButton(onClick = { confirmProtocol = null }) { Text("Cancel") } }) }
            if (pasted) PasteDialog({ pasted = false }) { text -> try { addProfiles(Imports.parse(listOf(text))); pasted = false } catch (e: Exception) { message = e.message ?: "Invalid profile." } }
            editing?.let { profile -> ProfileDialog(profile, { editing = null }) { changed -> val current = Repository.config.value; save(current.copy(profiles = current.profiles.filterNot { it.id == changed.id } + changed, selected = current.selected ?: changed.id)); editing = null } }
        }
    }

    @Composable private fun RulesPage(apps: Boolean, policy: Policy, changed: (Policy) -> Unit) {
        var adding by remember { mutableStateOf(false) }; var modeExpanded by remember { mutableStateOf(false) }
        Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Box { OutlinedButton(onClick = { modeExpanded = true }, Modifier.fillMaxWidth()) { Text(modeLabel(policy.mode)) }; DropdownMenu(modeExpanded, { modeExpanded = false }) { Action.entries.forEach { mode -> DropdownMenuItem(text = { Text(modeLabel(mode)) }, onClick = { changed(policy.copy(mode = mode)); modeExpanded = false }) } } }
            Button(onClick = { adding = true }, Modifier.fillMaxWidth()) { Text(if (apps) "Choose installed app" else "Add domain / IP") }
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) { items(policy.rules) { rule -> Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp)) { Text(rule.target, maxLines = 2); Row(verticalAlignment = Alignment.CenterVertically) { Text(rule.action.name, Modifier.weight(1f), color = MaterialTheme.colorScheme.primary); TextButton(onClick = { changed(policy.copy(rules = policy.rules.filterNot { it == rule })) }) { Text("Remove") } } } } } }
            if (policy.mode == Action.Disabled) Text("Rules off · saved exceptions are ignored.", fontSize = 12.sp, color = Color(0xFF9CAEC8))
        }
        if (adding) RuleDialog(apps, { adding = false }) { rule -> val next = policy.copy(rules = policy.rules.filterNot { it.target == rule.target } + rule); val c = Repository.config.value; try { EngineConfig.validate(if (apps) c.copy(apps = next) else c.copy(sites = next)); changed(next); adding = false } catch (e: Exception) { message = e.message ?: "Invalid rule." } }
    }
    @Composable private fun RuleDialog(apps: Boolean, dismiss: () -> Unit, add: (Rule) -> Unit) {
        var target by remember { mutableStateOf("") }; var action by remember { mutableStateOf(Action.Tunnel) }; var kind by remember { mutableStateOf("Domain") }
        val installed by produceState<List<Pair<String, String>>>(emptyList(), apps) { if (apps) value = withContext(Dispatchers.IO) { @Suppress("DEPRECATION") packageManager.getInstalledApplications(0).filter { it.packageName != packageName }.map { it.loadLabel(packageManager).toString() to it.packageName }.sortedBy { it.first.lowercase() } } }
        var search by remember { mutableStateOf("") }
        AlertDialog(onDismissRequest = dismiss, title = { Text(if (apps) "App exception" else "Site exception") }, text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row { listOf(Action.Tunnel, Action.Bypass, Action.Block).forEach { value -> FilterChip(action == value, { action = value }, label = { Text(value.name) }, modifier = Modifier.padding(end = 4.dp)) } }
                if (apps) {
                    OutlinedTextField(search, { search = it }, label = { Text("Search apps / packages") }, singleLine = true)
                    LazyColumn(Modifier.heightIn(max = 320.dp)) { items(installed.filter { search.isBlank() || it.first.contains(search, true) || it.second.contains(search, true) }) { (name, id) -> TextButton(onClick = { target = id }, Modifier.fillMaxWidth()) { Column(Modifier.fillMaxWidth()) { Text("${if (target == id) "✓ " else ""}$name"); Text(id, fontSize = 11.sp) } } } }
                } else {
                    Row { listOf("Domain", "IP").forEach { value -> FilterChip(kind == value, { kind = value }, label = { Text(value) }) } }
                    OutlinedTextField(target, { target = it.trim() }, label = { Text(if (kind == "Domain") "example.com" else "IP address or CIDR") }, singleLine = true)
                }
            }
        }, confirmButton = { TextButton(onClick = { add(Rule(target, action, if (apps) "App" else kind)) }, enabled = target.isNotBlank()) { Text("Add") } }, dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } })
    }
}

@Composable private fun Home(config: Config, state: TunnelState, dirty: Boolean, profiles: () -> Unit, protocol: (String) -> Unit, connect: () -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        val profile = config.profiles.find { it.id == config.selected }
        Card(onClick = profiles, modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = Color(0xFF243954))) { Row(Modifier.fillMaxWidth().padding(18.dp), verticalAlignment = Alignment.CenterVertically) { Text(profile?.let { flag(it.country) } ?: "◎", fontSize = 29.sp); Spacer(Modifier.width(14.dp)); Column(Modifier.weight(1f)) { Text(profile?.name ?: "Add a server profile", maxLines = 1); Text(if (profile == null) "Import JSON or share links" else "Manage server profiles", fontSize = 12.sp, color = Color(0xFF9FB5D4)) }; Text("›", fontSize = 28.sp) } }
        Box { OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) { Text(config.protocol, Modifier.weight(1f)); Text("⌄") }; DropdownMenu(expanded, { expanded = false }) { listOf("REALITY", "Hysteria 2").forEach { name -> DropdownMenuItem(text = { Text(name) }, onClick = { expanded = false; protocol(name) }, enabled = if (name == "REALITY") profile?.reality != null else profile?.hysteria != null) } } }
        Text(state.status, fontSize = 25.sp, color = if (state.status == "Connected") MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
        Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            Button(connect, modifier = Modifier.sizeIn(minWidth = 120.dp, minHeight = 120.dp, maxWidth = 190.dp, maxHeight = 190.dp).aspectRatio(1f), shape = CircleShape, colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF12BFA2), contentColor = Color(0xFF06272D))) { Text(if (dirty && state.active) "Apply &\nreconnect" else if (state.active) "Disconnect" else "Connect", fontSize = 22.sp, textAlign = androidx.compose.ui.text.style.TextAlign.Center) }
        }
        Text("Exit IP: ${state.exitIp}", color = MaterialTheme.colorScheme.primary)
        Text(if (dirty) "Settings changed · reconnect to apply" else state.detail.ifBlank { "Android TUN" }, fontSize = 12.sp, maxLines = 2, color = Color(0xFF9FB5D4))
        Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(15.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("TUNNEL TRAFFIC", fontSize = 11.sp, color = Color(0xFF9FB5D4))
            Row(Modifier.fillMaxWidth()) { Column(Modifier.weight(1f)) { Text("↑ ${bytes(state.uploadRate)}/s", color = Color(0xFF13C6A2), fontSize = 20.sp); Text("${bytes(state.uploaded)} sent", fontSize = 11.sp) }; Column(Modifier.weight(1f)) { Text("↓ ${bytes(state.downloadRate)}/s", color = Color(0xFF5C9FFF), fontSize = 20.sp); Text("${bytes(state.downloaded)} received", fontSize = 11.sp) } }
            Canvas(Modifier.fillMaxWidth().height(42.dp)) {
                val maximum = state.history.maxOfOrNull { maxOf(it.first, it.second) }?.coerceAtLeast(1) ?: 1
                for ((index, color) in listOf(Color(0xFF13C6A2), Color(0xFF5C9FFF)).withIndex()) for (i in 1 until state.history.size) { fun point(n: Int): Offset { val value = if (index == 0) state.history[n].first else state.history[n].second; return Offset(size.width * n / 59f, size.height - size.height * value.toFloat() / maximum) }; drawLine(color, point(i - 1), point(i), strokeWidth = 2.dp.toPx()) }
            }
        } }
    }
}
@Composable private fun SettingsRow(title: String, detail: String, click: () -> Unit) { Card(onClick = click, modifier = Modifier.fillMaxWidth()) { Row(Modifier.padding(17.dp), verticalAlignment = Alignment.CenterVertically) { Column(Modifier.weight(1f)) { Text(title, fontSize = 18.sp); if (detail.isNotEmpty()) Text(detail, fontSize = 12.sp, color = Color(0xFF9CAEC8)) }; Text("›", fontSize = 24.sp) } } }
@Composable private fun Logs(export: () -> Unit) {
    val logs by Repository.logs.collectAsStateWithLifecycle(); val formatter = remember { SimpleDateFormat("HH:mm:ss", Locale.US) }
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) { Row { OutlinedButton(onClick = { Repository.clearLogs() }) { Text("Clear") }; Spacer(Modifier.width(10.dp)); OutlinedButton(onClick = export) { Text("Export") } }; LazyColumn(Modifier.weight(1f).fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) { items(logs) { event -> Column { Text("${formatter.format(Date(event.time))}  ${event.level}  ${event.code}", fontSize = 12.sp, color = if (event.level == "ERROR") Color(0xFFFFBA9B) else Color(0xFF8FDACF)); if (event.detail.isNotEmpty()) Text(event.detail, fontSize = 12.sp) } } } }
}
@Composable private fun PasteDialog(dismiss: () -> Unit, add: (String) -> Unit) { var text by remember { mutableStateOf("") }; AlertDialog(onDismissRequest = dismiss, title = { Text("Import profile") }, text = { OutlinedTextField(text, { text = it }, label = { Text("VLESS / Hysteria links or export JSON") }, modifier = Modifier.height(220.dp)) }, confirmButton = { TextButton(onClick = { add(text) }) { Text("Import") } }, dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } }) }
@Composable private fun ProfileDialog(profile: Profile, dismiss: () -> Unit, save: (Profile) -> Unit) {
    var name by remember { mutableStateOf(profile.name) }; var country by remember { mutableStateOf(profile.country) }; var protocol by remember { mutableStateOf(if (profile.hysteria != null) "Hysteria 2" else "REALITY") }
    var reality by remember { mutableStateOf(profile.reality ?: "{}") }; var hysteria by remember { mutableStateOf(profile.hysteria ?: "{}") }; var error by remember { mutableStateOf("") }
    val current = JSONObject(if (protocol == "REALITY") reality else hysteria)
    fun set(field: String, value: String) { current.put(field, if (field == "port") value.toIntOrNull() ?: 0 else value); if (protocol == "REALITY") reality = current.toString() else hysteria = current.toString() }
    AlertDialog(onDismissRequest = dismiss, title = { Text("VPN configure") }, text = { Column(Modifier.heightIn(max = 520.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedTextField(name, { name = it }, label = { Text("Profile name") }, singleLine = true)
        OutlinedTextField(country, { country = it.uppercase().take(2) }, label = { Text("Country code, e.g. JP") }, singleLine = true)
        Row { listOf("REALITY", "Hysteria 2").forEach { p -> FilterChip(protocol == p, { protocol = p }, label = { Text(p) }) } }
        val fields = if (protocol == "REALITY") listOf("server" to "Server IP / hostname", "port" to "Port", "uuid" to "UUID", "sni" to "TLS server name", "publicKey" to "REALITY public key", "shortId" to "Short ID") else listOf("server" to "Server IP / hostname", "port" to "Port", "auth" to "Authentication", "sni" to "TLS server name", "pin" to "Certificate SHA-256 pin", "obfs" to "Salamander password (optional)")
        for ((key, label) in fields) OutlinedTextField(current.optString(key, if (key == "port") "443" else ""), { set(key, it) }, label = { Text(label) }, singleLine = true, visualTransformation = if (key in listOf("auth", "obfs", "uuid")) PasswordVisualTransformation() else VisualTransformation.None)
        if (error.isNotEmpty()) Text(error, color = Color(0xFFFFBA9B), fontSize = 12.sp)
    } }, confirmButton = { TextButton(onClick = { try { if (!current.has("port")) set("port", "443"); if (protocol == "Hysteria 2") { if (!current.has("pin")) set("pin", ""); if (!current.has("obfs")) set("obfs", "") }; if (protocol == "REALITY" && !current.has("shortId")) set("shortId", ""); Imports.validate(protocol, current); require(name.isNotBlank() && country.matches(Regex("[A-Z]{2}"))); save(profile.copy(name = name, country = country, reality = reality.takeIf { it != "{}" }, hysteria = hysteria.takeIf { it != "{}" })) } catch (e: Exception) { error = e.message ?: "Invalid profile." } }) { Text("Save") } }, dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } })
}
private fun flag(country: String) = if (country.length == 2 && country.all { it in 'A'..'Z' }) country.map { String(Character.toChars(0x1F1E6 + it.code - 'A'.code)) }.joinToString("") else "◎"
private fun modeLabel(action: Action) = if (action == Action.Disabled) "Rules off" else "${action.name} by default"
