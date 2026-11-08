package dev.personalvpn.client

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.luminance
import org.junit.Assert.*
import org.junit.Test

class ConnectionActionTest {
    @Test fun pendingWorkCanBeCancelledEvenWhenSettingsAreDirty() {
        for(status in listOf("Connecting","Verifying","Reconnecting","Waiting for network")) {
            for(dirty in listOf(false,true)) assertEquals(ConnectionAction.Cancel,connectionAction(TunnelState(status,active=true,busy=true),dirty))
        }
        assertEquals(ConnectionAction.Apply,connectionAction(TunnelState("Connected",active=true),true))
        assertEquals(ConnectionAction.Disconnect,connectionAction(TunnelState("Connected",active=true)))
        assertEquals(ConnectionAction.Connect,connectionAction(TunnelState()))
        assertEquals(ConnectionAction.Stopping,connectionAction(TunnelState("Disconnecting",active=true,busy=true),true))
    }
    @Test fun textContrastRemainsAtLeastFourPointFiveAcrossEveryButtonGradient() {
        for(status in listOf("Disconnected","Connecting","Verifying","Reconnecting","Disconnecting","Connected","Error")) {
            val colors=powerAppearance(TunnelState(status,active=status!="Disconnected",busy=status in listOf("Connecting","Verifying","Disconnecting")))
            for(background in listOf(lerp(colors.tint,Color.White,.12f),colors.tint,lerp(colors.tint,Color.Black,.12f))) {
                val light=maxOf(colors.content.luminance(),background.luminance())
                val dark=minOf(colors.content.luminance(),background.luminance())
                assertTrue("$status button contrast was too low",(light+.05f)/(dark+.05f)>=4.5f)
            }
        }
    }
}
