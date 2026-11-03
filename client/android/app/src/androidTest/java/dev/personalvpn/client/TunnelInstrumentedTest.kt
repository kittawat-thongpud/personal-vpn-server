package dev.personalvpn.client

import android.content.Context
import android.net.VpnService
import androidx.test.core.app.ApplicationProvider
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Until
import dev.personalvpn.personalvpn.Personalvpn
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test
import org.junit.Before
import org.junit.After
import org.junit.runner.RunWith
import java.net.NetworkInterface

@RunWith(AndroidJUnit4::class)
class TunnelInstrumentedTest {
    @Before fun enableFixtureDiagnostics() { Repository.instrumentedDiagnostics = true }
    @After fun disableFixtureDiagnostics() { Repository.instrumentedDiagnostics = false }
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private fun waitFor(reason: String, seconds: Int = 90, check: () -> Boolean) {
        val until = System.currentTimeMillis() + seconds * 1000L
        while (System.currentTimeMillis() < until) { if (check()) return; Thread.sleep(300) }
        error("$reason: ${Repository.state.value.status}; events=" + Repository.logs.value.takeLast(8).joinToString { "${it.code}:${it.detail}" })
    }
    private fun captureName() = NetworkInterface.getNetworkInterfaces().toList().firstOrNull { nic -> nic.inetAddresses.toList().any { it.hostAddress == "172.29.255.1" } }?.let { "${it.name}/${it.index}" }
    private fun probe(udp: Boolean = false): Boolean {
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        device.executeShellCommand("am force-stop dev.personalvpn.probe")
        device.executeShellCommand("run-as dev.personalvpn.probe rm -f files/result.json")
        device.executeShellCommand("am start -W -n dev.personalvpn.probe/.ProbeActivity --ez udp $udp")
        var result: JSONObject? = null
        waitFor("Companion network probe", 20) { val text = device.executeShellCommand("run-as dev.personalvpn.probe cat files/result.json"); result = runCatching { JSONObject(text) }.getOrNull(); result != null }
        return result!!.getBoolean("success")
    }
    @Test fun nativeCaptureProtocolsPoliciesAndLifecycle() {
        assertNull("The isolated emulator must have VPN permission before instrumentation.", VpnService.prepare(context))
        val fixture = JSONObject(String(java.util.Base64.getDecoder().decode(InstrumentationRegistry.getArguments().getString("fixture")!!)))
        val profile = Profile(name = "CI fixture", reality = fixture.getJSONObject("reality").toString(), hysteria = fixture.getJSONObject("hysteria").toString())
        var config = Config(listOf(profile), profile.id)
        var firstName: String? = null
        try {
            for (protocol in listOf("Hysteria 2", "REALITY")) {
                config = config.copy(protocol = protocol, apps = Policy(), sites = Policy(Action.Tunnel))
                Personalvpn.check(EngineConfig.compile(config, "10.0.2.2"))
                Repository.save(config)
                TunnelService.command(context, if (Repository.state.value.active) TunnelService.RECONNECT else TunnelService.CONNECT)
                waitFor("Authenticated and captured HTTPS: $protocol") { Repository.state.value.status == "Connected" && Repository.state.value.detail == protocol }
                assertNotEquals("—", Repository.state.value.exitIp)
                val name = captureName(); assertNotNull(name)
                if (firstName == null) firstName = name else assertEquals("Transport changes must retain TUN", firstName, name)
                assertTrue("External application HTTPS capture", probe())
                if (protocol == "Hysteria 2") assertTrue("UDP forwarding", probe(udp = true))
            }
            // A different UID is necessary: the VPN application's own control
            // traffic is deliberately reserved and cannot test application Block.
            config = config.copy(protocol = "Hysteria 2", apps = Policy(Action.Tunnel, listOf(Rule("dev.personalvpn.probe", Action.Block, "App"))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply app Block") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertFalse("App Block must not be an OS Bypass exclusion", probe())
            config = config.copy(apps = Policy(Action.Block, listOf(Rule("dev.personalvpn.probe", Action.Tunnel, "App"))), sites = Policy(Action.Block, listOf(Rule("google.com", Action.Block))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply App-over-Site policy") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertTrue("Explicit App Tunnel must beat Site Block", probe())
            config = config.copy(apps = Policy(Action.Block, listOf(Rule("dev.personalvpn.probe", Action.Bypass, "App"))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply App Bypass") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertTrue("Explicit App Bypass must use a working protected Direct socket", probe())
            assertEquals(firstName, captureName())
        } finally {
            TunnelService.command(context, TunnelService.DISCONNECT)
            waitFor("Disconnect capture cleanup", 40) { !Repository.state.value.active && captureName() == null }
        }
    }
    @Test fun encryptedStorageAndNativePinConfiguration() {
        val store = SecureStore(context)
        val original = Repository.config.value
        val profile = Profile(hysteria = """{"server":"192.0.2.1","port":443,"auth":"fixture-secret","sni":"fixture.local","pin":"${"ab".repeat(32)}"}""")
        val config = Config(listOf(profile), profile.id)
        try {
            store.save(config); assertEquals(config, store.load())
            val bytes = java.io.File(context.noBackupFilesDir, "profiles.enc").readBytes()
            assertFalse(String(bytes, Charsets.ISO_8859_1).contains("fixture-secret"))
            Personalvpn.check(EngineConfig.compile(config, "192.0.2.1"))
        } finally { store.save(original) }
    }
    @Test fun homeControlsFitInsideTheScreen() {
        ActivityScenario.launch(MainActivity::class.java).use {
            val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
            assertTrue(device.wait(Until.hasObject(By.text("Connect")), 15000))
            val connect = device.findObject(By.text("Connect")).visibleBounds
            assertTrue(connect.top >= 0 && connect.bottom <= device.displayHeight)
            assertTrue(device.hasObject(By.text("Settings")))
            assertTrue(device.hasObject(By.text("TUNNEL TRAFFIC")))
        }
    }
}
