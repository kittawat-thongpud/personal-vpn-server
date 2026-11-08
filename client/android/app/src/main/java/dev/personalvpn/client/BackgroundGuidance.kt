package dev.personalvpn.client

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.DateFormat
import java.util.Date

@Composable internal fun BackgroundGuidance(review: BackgroundReview,dismiss: ()->Unit,refresh: ()->Unit,open: (SettingsTarget)->Unit) {
    var checked by remember { mutableStateOf(false) }
    VpnSheet("Background reliability",dismiss) {
        Row(Modifier.fillMaxWidth(),horizontalArrangement=Arrangement.spacedBy(8.dp)) {
            Text("${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL} · Android ${android.os.Build.VERSION.RELEASE} (API ${android.os.Build.VERSION.SDK_INT})",Modifier.weight(1f),fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
            TextButton(onClick=dismiss,modifier=Modifier.heightIn(min=48.dp)) { Text("Not now") }
        }
        Text("These settings can affect a continuous VPN. Android checks are read now; device-specific items need manual review. Change settings yourself using the buttons below.",fontSize=12.sp)
        Text("Home keeps VPN running. Removing its Recent Apps task stops VPN.",fontSize=12.sp,color=MaterialTheme.colorScheme.secondary)
        review.previousExit?.let { exit ->
            Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp)) {
                Text("Last process exit: ${exit.reason}",fontWeight=FontWeight.SemiBold)
                Text(DateFormat.getDateTimeInstance().format(Date(exit.time)),fontSize=11.sp)
                Text("Recorded by Android. This is not proof that a battery setting caused the exit.",fontSize=11.sp)
            } }
        } ?: Text("No previous process-exit record is available from Android.",fontSize=11.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
        val attention=review.checks.filter { it.state!=CheckState.Good }
        if(attention.isEmpty())Text("Available Android checks look ready.")
        fun cards(rows: List<BackgroundCheck>): @Composable ()->Unit = { rows.forEach { row ->
            Card(Modifier.fillMaxWidth()) { Column(Modifier.padding(12.dp),verticalArrangement=Arrangement.spacedBy(6.dp)) {
                Text(row.title,fontWeight=FontWeight.SemiBold)
                Text(row.state.label,fontSize=11.sp,color=if(row.state==CheckState.Attention)MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.secondary)
                Text(row.detail,fontSize=12.sp)
                OutlinedButton(onClick={open(row.target)},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Open ${if(row.target==SettingsTarget.App)"App settings" else "settings"}") }
            } }
        } }
        cards(attention)()
        TextButton(onClick={checked=!checked},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(if(checked)"Hide checked items" else "Show checked items (${review.checks.count { it.state==CheckState.Good }})") }
        if(checked)cards(review.checks.filter { it.state==CheckState.Good })()
        OutlinedButton(onClick=refresh,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Check again") }
        Button(onClick=dismiss,modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Continue") }
    }
}
