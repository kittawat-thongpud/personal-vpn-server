package dev.personalvpn.client

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

@Composable internal fun SiteTunnelPage(policy: Policy, changed: (Policy) -> Boolean) {
    var editing by remember { mutableStateOf<Int?>(null) }
    var modeOpen by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    Column(Modifier.fillMaxSize(), verticalArrangement=Arrangement.spacedBy(8.dp)) {
        OutlinedButton(onClick={modeOpen=true},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(modeLabel(policy.mode)) }
        Button(onClick={editing=-1;error=null},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Add site group") }
        LazyColumn(Modifier.weight(1f),verticalArrangement=Arrangement.spacedBy(6.dp)) {
            itemsIndexed(policy.rules) { index,rule ->
                Card(onClick={editing=index;error=null},modifier=Modifier.fillMaxWidth()) {
                    Row(Modifier.fillMaxWidth().padding(start=10.dp,end=2.dp),verticalAlignment=Alignment.CenterVertically) {
                        SiteGlyph(rule.kind,Modifier.size(26.dp));Spacer(Modifier.width(10.dp))
                        Text(rule.displayName,Modifier.weight(1f),maxLines=1,overflow=TextOverflow.Ellipsis,fontSize=14.sp)
                        val color=when(rule.action) { Action.Block->MaterialTheme.colorScheme.error;Action.Bypass->MaterialTheme.colorScheme.secondary;else->MaterialTheme.colorScheme.primary }
                        Surface(color=MaterialTheme.colorScheme.surfaceContainerHighest,shape=MaterialTheme.shapes.small,modifier=Modifier.padding(start=6.dp)) { Text(rule.action.name,Modifier.padding(horizontal=6.dp,vertical=4.dp),color=color,fontSize=11.sp,maxLines=1) }
                        IconButton(onClick={editing=index;error=null},modifier=Modifier.size(48.dp)) { SiteGlyph("Edit",Modifier.size(18.dp),"Edit ${rule.displayName}") }
                        IconButton(onClick={changed(policy.copy(rules=policy.rules.filterIndexed { i,_->i!=index }))},modifier=Modifier.size(48.dp)) { SiteGlyph("Delete",Modifier.size(18.dp),"Delete ${rule.displayName}") }
                    }
                }
            }
        }
        if(policy.rules.isEmpty()) Text("No site groups yet.",color=MaterialTheme.colorScheme.onSurfaceVariant)
        if(policy.mode==Action.Disabled) Text("Rules off · saved exceptions are ignored.",fontSize=12.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
    }
    if(modeOpen) VpnSheet("Default site action",{modeOpen=false}) { Action.entries.forEach { mode -> TextButton(onClick={if(changed(policy.copy(mode=mode)))modeOpen=false},modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text(modeLabel(mode)) } } }
    editing?.let { index ->
        val original=policy.rules.getOrNull(index)
        SiteGroupSheet(original,error,{editing=null}) { rule ->
            try {
                val entries=policy.rules.toMutableList();if(index<0)entries.add(rule)else entries[index]=rule
                val next=policy.copy(rules=entries);SiteTargets.validate(next)
                if(changed(next))editing=null
            } catch(e: IllegalArgumentException) { error=e.message?:"Invalid site group." }
        }
    }
}

@Composable private fun SiteGroupSheet(original: Rule?, error: String?, dismiss: ()->Unit, save: (Rule)->Unit) {
    var name by remember(original) { mutableStateOf(original?.name?:"") }
    var target by remember(original) { mutableStateOf(original?.target?:"") }
    var kind by remember(original) { mutableStateOf(original?.kind?:"Domain") }
    var action by remember(original) { mutableStateOf(original?.action?:Action.Tunnel) }
    var inputError by remember { mutableStateOf<String?>(null) }
    val large=LocalDensity.current.fontScale>1.3f
    VpnSheet(if(original==null)"Add site group"else"Edit site group",dismiss) {
        OutlinedTextField(name,{if(it.length<=80)name=it},label={Text("Group name (optional)")},singleLine=true,modifier=Modifier.fillMaxWidth())
        Row(horizontalArrangement=Arrangement.spacedBy(8.dp)) { listOf("Domain","IP").forEach { value -> FilterChip(kind==value,{kind=value},label={Text(value)},modifier=Modifier.heightIn(min=48.dp)) } }
        OutlinedTextField(target,{if(it.length<=SiteTargets.INPUT_LIMIT)target=it},label={Text("Targets")},minLines=3,maxLines=6,modifier=Modifier.fillMaxWidth())
        Text(if(kind=="Domain")"Separate domains with commas or new lines. Subdomains are included."else"Separate IPs with commas or new lines. IPv4, IPv6, CIDR and trailing wildcards such as 2.2.2.* are supported.",fontSize=12.sp,color=MaterialTheme.colorScheme.onSurfaceVariant)
        val choices: @Composable ()->Unit = { listOf(Action.Tunnel,Action.Bypass,Action.Block).forEach { value -> FilterChip(action==value,{action=value},label={Text(value.name)},modifier=Modifier.heightIn(min=48.dp)) } }
        if(large) Column(content= { choices() }) else Row(horizontalArrangement=Arrangement.spacedBy(6.dp)) { choices() }
        (inputError?:error)?.let { Text(it,color=MaterialTheme.colorScheme.error) }
        Button(onClick={try { val next=Rule(SiteTargets.clean(kind,target),action,kind,name.trim());SiteTargets.validate(Policy(rules=listOf(next)));inputError=null;save(next) }catch(e:IllegalArgumentException){inputError=e.message}},enabled=target.isNotBlank(),modifier=Modifier.fillMaxWidth().heightIn(min=48.dp)) { Text("Save group") }
    }
}

@Composable private fun SiteGlyph(kind: String,modifier: Modifier=Modifier,description: String=kind) {
    val color=MaterialTheme.colorScheme.onSurfaceVariant
    Canvas(modifier.then(Modifier.semanticsDescription(description))) {
        val unit=size.minDimension/24f;val stroke=Stroke(1.5f*unit)
        fun line(x1:Float,y1:Float,x2:Float,y2:Float)=drawLine(color,Offset(x1*unit,y1*unit),Offset(x2*unit,y2*unit),1.5f*unit)
        when(kind) {
            "Domain" -> { drawCircle(color,10*unit,style=stroke);drawOval(color,Offset(8*unit,2*unit),Size(8*unit,20*unit),style=stroke);line(2f,12f,22f,12f) }
            "IP" -> { line(12f,6f,12f,11f);line(4f,11f,20f,11f);line(4f,11f,4f,16f);line(20f,11f,20f,16f);listOf(Offset(12f,3f),Offset(4f,19f),Offset(20f,19f)).forEach { drawCircle(color,2.5f*unit,Offset(it.x*unit,it.y*unit),style=stroke) } }
            "Edit" -> { line(4f,20f,8f,19f);line(8f,19f,21f,6f);line(21f,6f,17f,2f);line(17f,2f,4f,15f);line(4f,15f,4f,20f);line(14f,5f,18f,9f) }
            else -> { drawRect(color,Offset(6*unit,7*unit),Size(12*unit,15*unit),style=stroke);line(3f,6f,21f,6f);line(9f,3f,15f,3f);line(10f,10f,10f,18f);line(14f,10f,14f,18f) }
        }
    }
}

private fun Modifier.semanticsDescription(value: String)=this.then(Modifier.semantics { contentDescription=value })
