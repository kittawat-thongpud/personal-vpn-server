@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package dev.personalvpn.client

import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

@Composable fun AppTunnelPage(policy: Policy, changed: (Policy) -> Unit) {
    val context = LocalContext.current
    var refresh by remember { mutableIntStateOf(0) }
    val catalog by produceState(InstalledApps.peek(), refresh) { value = InstalledApps.load(context, refresh > 0) }
    var search by rememberSaveable { mutableStateOf("") }
    var showSystem by rememberSaveable { mutableStateOf(false) }
    var exceptionsOnly by rememberSaveable { mutableStateOf(false) }
    var selected by rememberSaveable { mutableStateOf(arrayListOf<String>()) }
    var action by rememberSaveable { mutableStateOf(Action.Bypass) }
    var expanded by remember { mutableStateOf(false) }
    var enableRules by remember { mutableStateOf(false) }
    val rules = remember(policy.rules) { policy.rules.associateBy { it.target } }
    val apps = remember(catalog, policy.rules) {
        val known = catalog.apps.map { it.packageName }.toSet()
        catalog.apps + policy.rules.filter { it.target !in known }.map { InstalledApp(it.target, it.target, false, false) }
    }
    val shown = remember(apps, showSystem, exceptionsOnly, search, rules) {
        apps.filter { app -> (showSystem || !app.system || app.packageName in rules) &&
            (!exceptionsOnly || app.packageName in rules) &&
            (search.isBlank() || app.name.contains(search, true) || app.packageName.contains(search, true)) }
    }
    val chosen = selected.toSet()
    fun select(id: String) { selected = ArrayList(if (id in chosen) selected.filterNot { it == id } else selected + id) }
    fun apply(base: Policy = policy) { changed(AppRules.apply(base, chosen, action)); selected = arrayListOf() }

    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Box {
            OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) { Text(modeLabel(policy.mode)) }
            DropdownMenu(expanded, { expanded = false }) { Action.entries.forEach { mode -> DropdownMenuItem(text = { Text(modeLabel(mode)) }, onClick = { changed(policy.copy(mode = mode)); expanded = false }) } }
        }
        OutlinedTextField(search, { search = it }, singleLine = true, label = { Text("Search installed apps") }, modifier = Modifier.fillMaxWidth())
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            FilterChip(exceptionsOnly, { exceptionsOnly = !exceptionsOnly }, label = { Text("Exceptions") })
            FilterChip(showSystem, { showSystem = !showSystem }, label = { Text("System apps") })
            Spacer(Modifier.weight(1f))
            TextButton(onClick = { refresh++ }, modifier = Modifier.semantics { contentDescription = "Refresh installed apps" }) { Text("↻", fontSize = 22.sp) }
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            val allShown = shown.isNotEmpty() && shown.all { it.packageName in chosen }
            Checkbox(allShown, { selected = ArrayList(if (allShown) selected.filterNot { id -> shown.any { it.packageName == id } } else (selected + shown.map { it.packageName }).distinct()) }, enabled = shown.isNotEmpty())
            Text("Select shown · ${shown.size}", Modifier.weight(1f), fontSize = 12.sp)
            if (selected.isNotEmpty()) TextButton(onClick = { selected = arrayListOf() }) { Text("Clear selection") }
        }
        Card(Modifier.weight(1f).fillMaxWidth(), shape = RoundedCornerShape(18.dp)) {
            when {
                !catalog.loaded && !catalog.failed -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(26.dp)) }
                catalog.failed && !catalog.loaded -> Box(Modifier.fillMaxSize().padding(20.dp), contentAlignment = Alignment.Center) { Text("Could not read installed apps. Tap ↻ to retry.") }
                shown.isEmpty() -> Box(Modifier.fillMaxSize().padding(20.dp), contentAlignment = Alignment.Center) { Text(if (exceptionsOnly) "No matching app exceptions" else "No matching apps") }
                else -> LazyColumn(Modifier.fillMaxSize()) {
                    items(shown, key = { it.packageName }) { app ->
                        Row(Modifier.fillMaxWidth().toggleable(app.packageName in chosen, role = Role.Checkbox, onValueChange = { select(app.packageName) }).semantics { contentDescription = "Select app ${app.packageName}" }.padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            AppIcon(app, refresh)
                            Column(Modifier.weight(1f)) {
                                Text(app.name, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(app.packageName, fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                val rule = rules[app.packageName]
                                Text(if (!app.installed) "Not installed · ${rule?.action?.name.orEmpty()}" else rule?.action?.name ?: "Use default", fontSize = 11.sp, color = if (rule == null) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.primary)
                            }
                            Checkbox(app.packageName in chosen, null)
                        }
                        HorizontalDivider(Modifier.padding(start = 64.dp), color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.35f))
                    }
                }
            }
        }
        Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(18.dp)) {
            Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("${selected.size} selected", Modifier.weight(1f), fontSize = 12.sp)
                    TextButton(onClick = { changed(AppRules.remove(policy, chosen)); selected = arrayListOf() }, enabled = chosen.any { it in rules }) { Text("Use default") }
                }
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                    listOf(Action.Tunnel, Action.Bypass, Action.Block).forEach { value -> FilterChip(action == value, { action = value }, label = { Text(value.name) }, modifier = Modifier.weight(1f)) }
                }
                Button(onClick = { if (policy.mode == Action.Disabled) enableRules = true else apply() }, enabled = selected.isNotEmpty(), modifier = Modifier.fillMaxWidth()) { Text("Apply to ${selected.size} apps") }
            }
        }
        if (policy.mode == Action.Disabled) Text("Rules off · app exceptions are paused", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (enableRules) AlertDialog(onDismissRequest = { enableRules = false }, title = { Text("Enable App Tunnel rules?") }, text = { Text("Tunnel other apps by default and apply ${action.name} to the ${selected.size} selected apps. Site exceptions still apply to other apps.") }, confirmButton = { TextButton(onClick = { apply(policy.copy(mode = Action.Tunnel)); enableRules = false }) { Text("Enable & apply") } }, dismissButton = { TextButton(onClick = { enableRules = false }) { Text("Cancel") } })
}

@Composable private fun AppIcon(app: InstalledApp, generation: Int) {
    val context = LocalContext.current
    val icon by produceState<ImageBitmap?>(null, app.packageName, generation) { value = InstalledApps.icon(context, app.packageName) }
    Surface(Modifier.size(40.dp), shape = RoundedCornerShape(10.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
        val current = icon
        if (current != null) Image(current, contentDescription = null, modifier = Modifier.fillMaxSize())
        else Box(contentAlignment = Alignment.Center) { Text(app.name.take(1).uppercase(), fontWeight = FontWeight.Bold) }
    }
}
