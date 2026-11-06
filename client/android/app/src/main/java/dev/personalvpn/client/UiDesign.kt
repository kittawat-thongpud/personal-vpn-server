@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package dev.personalvpn.client

import android.graphics.Paint
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.*
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.max

internal val VpnScheme = darkColorScheme(
    primary=Color(0xFF32D4B2), onPrimary=Color(0xFF042F2A), secondary=Color(0xFF80B4FA),
    background=Color(0xFF0B1424), surface=Color(0xFF17263B), surfaceContainer=Color(0xFF192B42),
    surfaceContainerHigh=Color(0xFF233650), onSurface=Color(0xFFECF2FF), onSurfaceVariant=Color(0xFFA3B4CD),
    outline=Color(0xFF40546E), outlineVariant=Color(0xFF2D4058))
internal val VpnBackground = Brush.verticalGradient(listOf(Color(0xFF17283F),Color(0xFF0B1424),Color(0xFF101D30)))

@Composable internal fun BoxScope.ThinScrollbar(state: LazyListState) {
    Canvas(Modifier.align(Alignment.CenterEnd).fillMaxHeight().width(3.dp)) {
        val info=state.layoutInfo
        if(!state.isScrollInProgress || info.totalItemsCount<=info.visibleItemsInfo.size)return@Canvas
        val fraction=info.visibleItemsInfo.size.toFloat()/info.totalItemsCount
        val thumb=max(24.dp.toPx(),size.height*fraction)
        val first=info.visibleItemsInfo.firstOrNull()?:return@Canvas
        val progress=(first.index+(-first.offset.toFloat()/first.size.coerceAtLeast(1)))/(info.totalItemsCount-info.visibleItemsInfo.size).coerceAtLeast(1)
        drawRoundRect(Color(0xFF6581A3),Offset(0f,(size.height-thumb)*progress.coerceIn(0f,1f)),androidx.compose.ui.geometry.Size(size.width,thumb),androidx.compose.ui.geometry.CornerRadius(size.width))
    }
}

@Composable internal fun VpnSheet(title: String, dismiss: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
    ModalBottomSheet(onDismissRequest=dismiss, sheetState=rememberModalBottomSheetState(skipPartiallyExpanded=true),containerColor=VpnScheme.surface) {
        Column(Modifier.fillMaxWidth().heightIn(max=620.dp).imePadding().padding(horizontal=22.dp).verticalScroll(rememberScrollState()),verticalArrangement=Arrangement.spacedBy(12.dp)) {
            Text(title,fontSize=23.sp,fontWeight=FontWeight.SemiBold); content(); Spacer(Modifier.height(20.dp))
        }
    }
}

@Composable internal fun MenuIcon(kind: String) {
    Surface(Modifier.size(42.dp),shape=RoundedCornerShape(13.dp),color=Color(0xFF233E59)) {
        Canvas(Modifier.padding(10.dp)) {
            val c=Color(0xFF93C5FF); val w=1.8.dp.toPx()
            fun line(x: Float,y: Float,a: Float,b: Float)=drawLine(c,Offset(size.width*x,size.height*y),Offset(size.width*a,size.height*b),w,StrokeCap.Round)
            when(kind) {
                "App Tunnel" -> for(x in listOf(.12f,.60f)) for(y in listOf(.12f,.60f)) drawRoundRect(c,Offset(size.width*x,size.height*y),androidx.compose.ui.geometry.Size(size.width*.28f,size.height*.28f),androidx.compose.ui.geometry.CornerRadius(2.dp.toPx()),style=Stroke(w))
                "Site Tunnel" -> { drawCircle(c,size.minDimension*.42f,style=Stroke(w));line(.08f,.5f,.92f,.5f);drawOval(c,Offset(size.width*.32f,size.height*.08f),androidx.compose.ui.geometry.Size(size.width*.36f,size.height*.84f),style=Stroke(w)) }
                "Backend logs" -> for(y in listOf(.2f,.5f,.8f)) { line(.08f,y,.12f,y);line(.3f,y,.92f,y) }
                else -> { line(.1f,.25f,.9f,.25f);line(.1f,.75f,.9f,.75f);drawCircle(c,size.minDimension*.1f,Offset(size.width*.35f,size.height*.25f));drawCircle(c,size.minDimension*.1f,Offset(size.width*.7f,size.height*.75f)) }
            }
        }
    }
}

@Composable internal fun PowerButton(state: TunnelState,dirty: Boolean,click: () -> Unit) {
    val tint=when { state.status=="Connected"->VpnScheme.primary;state.status=="Error"->Color(0xFFF38F98);state.active->Color(0xFFF2C577);else->VpnScheme.secondary }
    Box(Modifier.fillMaxSize(),contentAlignment=Alignment.Center) {
        Box(Modifier.sizeIn(maxWidth=202.dp,maxHeight=202.dp).aspectRatio(1f).background(Brush.radialGradient(listOf(tint.copy(alpha=.24f),Color.Transparent)),CircleShape).padding(12.dp)) {
            Button(onClick=click,enabled=!state.busy,contentPadding=PaddingValues(10.dp),shape=CircleShape,
                modifier=Modifier.fillMaxSize().shadow(12.dp,CircleShape).background(Brush.verticalGradient(listOf(lerp(tint,Color.White,.2f),tint,lerp(tint,Color.Black,.12f))),CircleShape).border(1.dp,Color.White.copy(alpha=.28f),CircleShape),
                colors=ButtonDefaults.buttonColors(containerColor=Color.Transparent,contentColor=Color(0xFF082B31))) {
                Column(horizontalAlignment=Alignment.CenterHorizontally,verticalArrangement=Arrangement.spacedBy(10.dp)) {
                    Icon(painterResource(R.drawable.ic_notification),null,Modifier.size(34.dp))
                    Text(if(dirty&&state.active) "Apply & reconnect" else if(state.active) "Disconnect" else "Connect",fontSize=19.sp,textAlign=androidx.compose.ui.text.style.TextAlign.Center)
                }
            }
        }
    }
}

@Composable internal fun TrafficGraph(history: List<Pair<Long,Long>>,modifier: Modifier=Modifier) {
    val scale=remember(history.isEmpty()) { ChartScale() }
    val peak=remember(history) { scale.update(history.maxOfOrNull { max(it.first,it.second).toDouble() }?:0.0) }
    val density=LocalDensity.current
    val paint=remember(density) { Paint(Paint.ANTI_ALIAS_FLAG).apply { color=0xFF8B9EBB.toInt();textSize=with(density){9.sp.toPx()} } }
    Canvas(modifier.fillMaxWidth().height(82.dp).semantics { contentDescription="Traffic over 60 seconds; shared upload and download scale" }) {
        val left=max(52.dp.toPx(),paint.measureText(bytes(peak.toLong())+"/s")+6.dp.toPx());val bottom=size.height-17.dp.toPx();val top=8.dp.toPx();val right=size.width-6.dp.toPx()
        val h=max(1f,bottom-top);val width=max(1f,right-left)
        for(n in 0..2) { val y=bottom-h*n/2f;drawLine(Color(0xFF2B4058),Offset(left,y),Offset(right,y),.6.dp.toPx());drawContext.canvas.nativeCanvas.drawText(if(n==0) "0" else bytes((peak*n/2).toLong())+"/s",0f,y+3.dp.toPx(),paint) }
        for(n in 0..4) {
            val x=left+width*n/4f;drawLine(Color(0xFF24384F),Offset(x,top),Offset(x,bottom),.6.dp.toPx())
            paint.textAlign=when(n){0->Paint.Align.LEFT;4->Paint.Align.RIGHT;else->Paint.Align.CENTER}
            drawContext.canvas.nativeCanvas.drawText(if(n==4) "now" else "−${60-15*n}s",x,size.height-2.dp.toPx(),paint)
        };paint.textAlign=Paint.Align.LEFT
        val values=history.takeLast(60);if(values.size<2)return@Canvas
        for(up in listOf(true,false)) {
            val color=if(up)Color(0xFF2AC5A6)else Color(0xFF6E9FE4)
            val points=values.mapIndexed { i,v->Offset(left+width*(60-values.size+i)/59f,bottom-h*((if(up)v.first else v.second)/peak).toFloat().coerceIn(0f,1f)) }
            val line=Path().apply { moveTo(points[0].x,points[0].y);points.zipWithNext().forEach { (a,b)->val mid=(a.x+b.x)/2;cubicTo(mid,a.y,mid,b.y,b.x,b.y) } }
            val area=Path().apply { addPath(line);lineTo(points.last().x,bottom);lineTo(points.first().x,bottom);close() }
            drawPath(area,Brush.verticalGradient(listOf(color.copy(alpha=.16f),color.copy(alpha=.015f)),top,bottom));drawPath(line,color.copy(alpha=.85f),style=Stroke(1.6.dp.toPx(),cap=StrokeCap.Round))
        }
    }
}
