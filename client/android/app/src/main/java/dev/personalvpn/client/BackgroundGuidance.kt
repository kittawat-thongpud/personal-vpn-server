package dev.personalvpn.client

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.DateFormat
import java.util.Date

@Composable internal fun BackgroundGuidance(review: BackgroundReview,automatic: Boolean,confirmed: Set<String>,dismiss: ()->Unit,refresh: ()->Unit,open: (SettingsTarget)->Unit,confirm: (String,Boolean)->Unit) {
    var manual by remember { mutableStateOf(false) }
    var checked by remember { mutableStateOf(false) }
    var unavailable by remember { mutableStateOf(false) }
    var exitDetail by remember { mutableStateOf(false) }
    VpnSheet("Background reliability",dismiss) {
        Text("Only detected issues are shown automatically.",fontSize=12.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
        if(review.detectedIssues.isEmpty())Text("No detected issues.",color=MaterialTheme.colorScheme.secondary)
        review.detectedIssues.forEach { BackgroundCheckCard(it,open) }
        if(!automatic) {
            val pending=review.pendingManual(confirmed)
            if(pending.isNotEmpty()) {
                TextButton(onClick={manual=!manual},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(if(manual)"Hide device checklist" else "Device checklist (${pending.size})") }
                if(manual) {
                    Text("Optional checks. Menu names may vary. Tap I've set this after checking.",fontSize=12.sp)
                    pending.forEach { row -> BackgroundCheckCard(row,open) {
                        TextButton(onClick={confirm(row.id,true)},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp).semantics { contentDescription="Confirm ${row.title} is set" }) { Text("I've set this") }
                    } }
                }
            }
            val configured=review.checks.filter { it.state==CheckState.Good || (it.state==CheckState.Manual && it.id in confirmed) }
            if(configured.isNotEmpty()) {
                TextButton(onClick={checked=!checked},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(if(checked)"Hide configured settings" else "Configured settings (${configured.size})") }
                if(checked)configured.forEach { row ->
                    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp)) {
                        Text(row.title,fontWeight=FontWeight.SemiBold)
                        Text(if(row.state==CheckState.Manual)"Confirmed by you" else "Configured",fontSize=12.sp,color=MaterialTheme.colorScheme.secondary)
                        if(row.state==CheckState.Manual)TextButton(onClick={confirm(row.id,false)},modifier=Modifier.heightIn(min=48.dp)) { Text("Review again") }
                    } }
                }
            }
            val unknown=review.checks.filter { it.state==CheckState.Unknown }
            if(unknown.isNotEmpty()) {
                TextButton(onClick={unavailable=!unavailable},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(if(unavailable)"Hide unavailable checks" else "Could not check (${unknown.size})") }
                if(unavailable) {
                    Text("These aren't confirmed problems. You can check them yourself.",fontSize=12.sp)
                    unknown.forEach { BackgroundCheckCard(it,open) }
                }
            }
            review.previousExit?.let { exit ->
                TextButton(onClick={exitDetail=!exitDetail},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Last app exit") }
                if(exitDetail) {
                    Text(exit.reason,fontWeight=FontWeight.SemiBold)
                    Text(DateFormat.getDateTimeInstance().format(Date(exit.time)),fontSize=12.sp)
                }
            }
        }
        OutlinedButton(onClick=refresh,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Check again") }
        Button(onClick=dismiss,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Done") }
    }
}

@Composable private fun BackgroundCheckCard(row: BackgroundCheck,open: (SettingsTarget)->Unit,extra: (@Composable ()->Unit)? = null) {
    Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(14.dp),verticalArrangement=Arrangement.spacedBy(10.dp)) {
        Text(row.title,fontWeight=FontWeight.SemiBold)
        row.steps.forEachIndexed { index,step ->
            Row(horizontalArrangement=Arrangement.spacedBy(10.dp)) {
                Text("${index+1}.",color=MaterialTheme.colorScheme.secondary,fontSize=13.sp)
                Text(step,Modifier.weight(1f),fontSize=13.sp)
            }
        }
        OutlinedButton(onClick={open(row.target)},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(if(row.target==SettingsTarget.App)"Open app settings" else "Open settings") }
        extra?.invoke()
    } }
}
