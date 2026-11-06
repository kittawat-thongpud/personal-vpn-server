@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package dev.personalvpn.client

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.nestedscroll.NestedScrollConnection
import androidx.compose.ui.input.nestedscroll.NestedScrollSource
import androidx.compose.ui.input.nestedscroll.nestedScroll
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
    var actionsOpen by remember { mutableStateOf(false) }
    var controlsVisible by remember { mutableStateOf(true) }
    var searchFocused by remember { mutableStateOf(false) }
    var details by remember { mutableStateOf<InstalledApp?>(null) }
    var saved by remember { mutableStateOf(false) }
    val listState = rememberLazyListState()
    val scroll = remember { object : NestedScrollConnection {
        override fun onPreScroll(available: Offset, source: NestedScrollSource): Offset {
            if (source == NestedScrollSource.UserInput && !searchFocused) {
                if (available.y < -4f) controlsVisible = false
                if (available.y > 4f) controlsVisible = true
            }
            return Offset.Zero
        }
    } }
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
    val duplicateNames = remember(apps) { apps.groupingBy { it.name }.eachCount().filterValues { it > 1 }.keys }
    val chosen = selected.toSet()
    fun select(id: String) { saved = false; selected = ArrayList(if (id in chosen) selected.filterNot { it == id } else selected + id) }
    fun apply(base: Policy = policy) { changed(AppRules.apply(base, chosen, action)); selected = arrayListOf(); actionsOpen = false; saved = true }

    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Box {
            OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) { Text(modeLabel(policy.mode)) }
        }
        AnimatedVisibility(controlsVisible || searchFocused) {
            Column {
                OutlinedTextField(search, { search = it }, singleLine = true, label = { Text("Search installed apps") }, modifier = Modifier.fillMaxWidth().onFocusChanged { searchFocused = it.isFocused })
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    FilterChip(exceptionsOnly, { exceptionsOnly = !exceptionsOnly }, label = { Text("Exceptions") })
                    FilterChip(showSystem, { showSystem = !showSystem }, label = { Text("System apps") })
                    Spacer(Modifier.weight(1f))
                    TextButton(onClick = { refresh++ }, modifier = Modifier.sizeIn(minWidth=48.dp,minHeight=48.dp).semantics { contentDescription = "Refresh installed apps" }) { Text("↻", fontSize = 22.sp) }
                }
            }
        }
        Row(Modifier.fillMaxWidth().heightIn(min=48.dp), verticalAlignment = Alignment.CenterVertically) {
            val allShown = shown.isNotEmpty() && shown.all { it.packageName in chosen }
            Checkbox(allShown, { selected = ArrayList(if (allShown) selected.filterNot { id -> shown.any { it.packageName == id } } else (selected + shown.map { it.packageName }).distinct()) }, enabled = shown.isNotEmpty())
            Text("Select shown · ${shown.size}", Modifier.weight(1f), fontSize = 12.sp)
            if (!controlsVisible && !searchFocused) TextButton(onClick={ controlsVisible=true }) { Text("Search") }
        }
        Card(Modifier.weight(1f).fillMaxWidth(), shape = RoundedCornerShape(18.dp)) {
            when {
                !catalog.loaded && !catalog.failed -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(26.dp)) }
                catalog.failed && !catalog.loaded -> Box(Modifier.fillMaxSize().padding(20.dp), contentAlignment = Alignment.Center) { Text("Could not read installed apps. Tap ↻ to retry.") }
                shown.isEmpty() -> Box(Modifier.fillMaxSize().padding(20.dp), contentAlignment = Alignment.Center) { Text(if (exceptionsOnly) "No matching app exceptions" else "No matching apps") }
                else -> Box(Modifier.fillMaxSize()) {
                    LazyColumn(Modifier.fillMaxSize().nestedScroll(scroll), state=listState) {
                    items(shown, key = { it.packageName }) { app ->
                        Row(Modifier.fillMaxWidth().toggleable(app.packageName in chosen, role = Role.Checkbox, onValueChange = { select(app.packageName) }).semantics { contentDescription = "Select app ${app.packageName}" }.heightIn(min=64.dp).padding(horizontal = 12.dp, vertical = 5.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            AppIcon(app, refresh)
                            Column(Modifier.weight(1f)) {
                                Text(app.name, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                if (!app.installed) Text("Not installed",fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
                                else if (app.name in duplicateNames) Text(app.packageName,fontSize=10.sp,maxLines=1,overflow=TextOverflow.Ellipsis)
                            }
                            rules[app.packageName]?.let { rule -> Surface(color=MaterialTheme.colorScheme.primary.copy(alpha=.12f),shape=RoundedCornerShape(7.dp)) { Text(rule.action.name,Modifier.padding(6.dp),fontSize=11.sp,color=MaterialTheme.colorScheme.primary) } }
                            TextButton(onClick={ details=app },modifier=Modifier.sizeIn(minWidth=48.dp,minHeight=48.dp).semantics { contentDescription="App details ${app.packageName}" },contentPadding=PaddingValues(0.dp)) { Text("ⓘ") }
                            Checkbox(app.packageName in chosen, null)
                        }
                        HorizontalDivider(Modifier.padding(start = 64.dp), color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.35f))
                    }
                    }
                    ThinScrollbar(listState)
                }
            }
        }
        if (selected.isNotEmpty()) Surface(shape=RoundedCornerShape(18.dp),color=MaterialTheme.colorScheme.surfaceContainerHigh,tonalElevation=4.dp) {
            Row(Modifier.fillMaxWidth().padding(horizontal=12.dp,vertical=4.dp),verticalAlignment=Alignment.CenterVertically) {
                Text("${selected.size} selected",Modifier.weight(1f),fontWeight=FontWeight.Medium)
                TextButton(onClick={ selected=arrayListOf() }) { Text("Clear") }
                Button(onClick={ actionsOpen=true }) { Text("Choose action") }
            }
        }
        if (saved) Text(if(Repository.state.value.active) "Saved · reconnect to apply" else "Saved",fontSize=11.sp,color=MaterialTheme.colorScheme.primary)
        if (policy.mode == Action.Disabled) Text("Rules off · app exceptions are paused", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if (expanded) VpnSheet("Default app action",{ expanded=false }) { Action.entries.forEach { mode -> TextButton(onClick={ changed(policy.copy(mode=mode));expanded=false },modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(modeLabel(mode)) } } }
    if (actionsOpen && selected.isNotEmpty()) VpnSheet("${selected.size} selected apps",{ actionsOpen=false }) {
        Text("App exceptions take priority over site rules.",fontSize=12.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(6.dp)) { listOf(Action.Tunnel,Action.Bypass,Action.Block).forEach { value -> FilterChip(action==value,{action=value},label={Text(value.name)},modifier=Modifier.weight(1f).heightIn(min=48.dp)) } }
        Button(onClick={ if(policy.mode==Action.Disabled) { actionsOpen=false;enableRules=true } else apply() },modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Apply to ${selected.size} apps") }
        OutlinedButton(onClick={ changed(AppRules.remove(policy,chosen));selected=arrayListOf();actionsOpen=false;saved=true },enabled=chosen.any { it in rules },modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Use default") }
    }
    details?.let { app -> VpnSheet(app.name,{details=null}) { Text(app.packageName); Text(if(app.installed) "Installed app" else "App is not installed"); Text("Action: ${rules[app.packageName]?.action?.name ?: "Use default"}") } }
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
