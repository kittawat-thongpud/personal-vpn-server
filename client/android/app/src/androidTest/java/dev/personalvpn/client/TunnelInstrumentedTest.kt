package dev.personalvpn.client

import android.content.Context
import android.content.Intent
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
    @Before fun enableFixtureDiagnostics() { Repository.instrumentedDiagnostics = true; Repository.instrumentedCertificate = fixture().getString("ca") }
    @After fun disableFixtureDiagnostics() { Repository.instrumentedHealthFailure = 0; Repository.instrumentedDiagnostics = false; Repository.instrumentedCertificate = null }
    private fun fixture() = JSONObject(String(java.util.Base64.getDecoder().decode(InstrumentationRegistry.getArguments().getString("fixture")!!)))
    private val context: Context get() = ApplicationProvider.getApplicationContext()
    private fun launchHome(): ActivityScenario<MainActivity> {
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        device.wakeUp(); device.pressHome()
        // Network tests launch a separate-UID companion and widget capability.
        // A UI case must start with its own fresh Activity task, irrespective of
        // which task those integration checks left in the foreground.
        return ActivityScenario.launch(Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK))
    }
    private fun uiHierarchy(): String = runCatching {
        val output = java.io.ByteArrayOutputStream()
        UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).dumpWindowHierarchy(output)
        output.toString("UTF-8")
    }.getOrDefault("UI hierarchy unavailable")
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
        val ca = java.util.Base64.getEncoder().encodeToString(fixture().getString("ca").toByteArray())
        device.executeShellCommand("am start -W -n dev.personalvpn.probe/.ProbeActivity --ez udp $udp --es ca $ca")
        var result: JSONObject? = null
        waitFor("Companion network probe", 20) { val text = device.executeShellCommand("run-as dev.personalvpn.probe cat files/result.json"); result = runCatching { JSONObject(text) }.getOrNull(); result != null }
        return result!!.getBoolean("success")
    }
    @Test fun bulkInstalledAppSelectionAndExceptionEditing() {
        val original = Repository.config.value
        val ids = listOf("dev.personalvpn.probe", "dev.personalvpn.client.test")
        Repository.save(original.copy(apps = Policy(Action.Tunnel)))
        try {
            launchHome().use {
                val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
                val settings = device.wait(Until.findObject(By.text("Settings")), 15000)
                    ?: error("Settings missing after fresh launch; ${uiHierarchy()}")
                settings.click()
                device.wait(Until.findObject(By.text("App Tunnel")), 15000).click()
                device.wait(Until.findObject(By.clazz("android.widget.EditText")), 15000).text = "dev.personalvpn"
                fun selectBoth() { ids.forEach { id -> device.wait(Until.findObject(By.desc("Select app $id")), 20000).click() } }
                assertNull("No action bar before selection",device.findObject(By.text("Choose action")))
                selectBoth()
                device.wait(Until.findObject(By.text("Choose action")),10000).click()
                device.wait(Until.findObject(By.text("Block")),10000).click()
                device.findObject(By.text("Apply to 2 apps")).click()
                waitFor("Bulk App Block saved", 10) { Repository.config.value.apps.rules.size == 2 && Repository.config.value.apps.rules.all { it.action == Action.Block } }
                device.takeScreenshot(java.io.File(context.filesDir, "app-tunnel.png"))
                selectBoth()
                device.wait(Until.findObject(By.text("Choose action")),10000).click()
                device.wait(Until.findObject(By.text("Bypass")),10000).click()
                device.findObject(By.text("Apply to 2 apps")).click()
                waitFor("Bulk App Bypass replaces rules", 10) { Repository.config.value.apps.rules.size == 2 && Repository.config.value.apps.rules.all { it.action == Action.Bypass } }
                selectBoth()
                device.wait(Until.findObject(By.text("Choose action")),10000).click()
                device.wait(Until.findObject(By.text("Use default")),10000).click()
                waitFor("Bulk exceptions removed", 10) { Repository.config.value.apps.rules.isEmpty() }
                assertEquals(Action.Tunnel, Repository.config.value.apps.mode)
            }
        } finally { Repository.save(original) }
    }
    @Test fun nativeCaptureProtocolsPoliciesAndLifecycle() {
        assertNull("The isolated emulator must have VPN permission before instrumentation.", VpnService.prepare(context))
        val fixture = fixture()
        val profile = Profile(name = "CI fixture", reality = fixture.getJSONObject("reality").toString(), hysteria = fixture.getJSONObject("hysteria").toString())
        var config = Config(listOf(profile), profile.id)
        var firstName: String? = null
        val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
        try {
            for (protocol in listOf("Hysteria 2", "REALITY")) {
                config = config.copy(protocol = protocol, apps = Policy(), sites = Policy(Action.Tunnel))
                Personalvpn.check(EngineConfig.compile(config, "10.0.2.2", true))
                Repository.save(config)
                TunnelService.command(context, if (Repository.state.value.active) TunnelService.RECONNECT else TunnelService.CONNECT)
                waitFor("Authenticated and captured HTTPS: $protocol") { Repository.state.value.status == "Connected" && Repository.state.value.detail == protocol }
                assertNotEquals("—", Repository.state.value.exitIp)
                val name = captureName(); assertNotNull(name)
                if (firstName == null) firstName = name else assertEquals("Transport changes must retain TUN", firstName, name)
                assertTrue("External application HTTPS capture", probe())
                // Make only the first real HTTPS target fail. Fallback must
                // preserve the same core and independently prove OS capture.
                val stopped = Repository.logs.value.count { it.code == "core.stopped" }
                val fallbacks = Repository.logs.value.count { it.code == "health.fallback_verified" }
                Repository.instrumentedHealthFailure = 1
                waitFor("Fallback health target", 40) { Repository.logs.value.count { it.code == "health.fallback_verified" } > fallbacks && Repository.state.value.status == "Connected" }
                assertEquals("An endpoint outage must not restart the core", stopped, Repository.logs.value.count { it.code == "core.stopped" })
                println("Recovery verified: $protocol primary-target outage retained the core")
                Repository.instrumentedHealthFailure = 0
                if (protocol == "Hysteria 2") {
                    Repository.instrumentedHealthFailure = 2
                    waitFor("Repeated failure enters degraded before restarting", 40) { Repository.state.value.status == "Degraded" }
                    assertEquals("First complete health failure retains core", stopped, Repository.logs.value.count { it.code == "core.stopped" })
                    waitFor("Consecutive failure restarts transport", 30) { Repository.logs.value.count { it.code == "core.stopped" } > stopped }
                    Repository.instrumentedHealthFailure = 0
                    waitFor("Automatic recovery without a user command") { Repository.state.value.status == "Connected" }
                    assertEquals(firstName, captureName())
                    println("Recovery verified: consecutive health failures recovered without a user command")
                }
                // Actual emulator connectivity loss/restoration, no user
                // Reconnect command, with the capture descriptor retained.
                device.executeShellCommand("svc wifi disable")
                device.executeShellCommand("svc data disable")
                waitFor("Physical network loss", 25) { Repository.state.value.status == "Waiting for network" }
                assertEquals("Capture must remain during upstream loss", firstName, captureName())
                device.executeShellCommand("svc data enable")
                waitFor("Automatic recovery after physical network restoration") { Repository.state.value.status == "Connected" && Repository.state.value.detail == protocol }
                assertEquals("Network recovery must reuse capture", firstName, captureName())
                assertTrue("Companion traffic after recovery", probe())
                println("Recovery verified: $protocol network loss to cellular restored separate-UID HTTPS and retained TUN")
                val transitions = Repository.logs.value.count { it.code == "network.changed" }
                device.executeShellCommand("svc wifi enable")
                device.executeShellCommand("svc data disable")
                waitFor("Automatic recovery back to Wi-Fi") { Repository.logs.value.count { it.code == "network.changed" } > transitions && Repository.state.value.status == "Connected" }
                assertEquals(firstName, captureName())
                println("Recovery verified: $protocol return to Wi-Fi retained TUN")
                waitFor("Live tunnel counters", 10) { Repository.state.value.uploaded > 0 && Repository.state.value.downloaded > 0 }
                if (protocol == "Hysteria 2") {
                    assertTrue("UDP forwarding", probe(udp = true))
                    UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).executeShellCommand("am start -W -n dev.personalvpn.client/.MainActivity -a dev.personalvpn.DISCONNECT")
                    Thread.sleep(500)
                    assertTrue("Untrusted exported Activity command must not disconnect VPN", Repository.state.value.active)
                }
            }
            // A different UID is necessary: the VPN application's own control
            // traffic is deliberately reserved and cannot test application Block.
            config = config.copy(protocol = "Hysteria 2", apps = Policy(Action.Tunnel, listOf(Rule("dev.personalvpn.probe", Action.Block, "App"))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply app Block") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertFalse("App Block must not be an OS Bypass exclusion", probe())
            config = config.copy(apps = Policy(Action.Block, listOf(Rule("dev.personalvpn.probe", Action.Tunnel, "App"))), sites = Policy(Action.Block, listOf(Rule("fixture.local", Action.Block))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply App-over-Site policy") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertTrue("Explicit App Tunnel must beat Site Block", probe())
            config = config.copy(apps = Policy(Action.Block, listOf(Rule("dev.personalvpn.probe", Action.Bypass, "App"))))
            Repository.save(config); TunnelService.command(context, TunnelService.RECONNECT)
            waitFor("Apply App Bypass") { Repository.state.value.status == "Connected" && Repository.state.value.appliedConfig == config.fingerprint() }
            assertTrue("Explicit App Bypass must use a working protected Direct socket", probe())
            assertEquals(firstName, captureName())
        } finally {
            device.executeShellCommand("svc data enable"); device.executeShellCommand("svc wifi enable")
            // Bring the app foreground before sending our widget capability:
            // a test process in background has no user-tap BAL privilege.
            UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).executeShellCommand("am start -W -n dev.personalvpn.client/.MainActivity")
            Surfaces.activity(context, TunnelService.DISCONNECT, 99).send()
            waitFor("Disconnect capture cleanup", 40) { !Repository.state.value.active && captureName() == null }
            assertEquals("—", Repository.state.value.exitIp)
            assertEquals(0L, Repository.state.value.uploadRate)
            assertEquals(0L, Repository.state.value.downloadRate)
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
        launchHome().use {
            val device = UiDevice.getInstance(InstrumentationRegistry.getInstrumentation())
            if (!device.wait(Until.hasObject(By.text("Connect")), 15000)) error("Connect missing after fresh launch; ${uiHierarchy()}")
            val connect = device.findObject(By.text("Connect")).visibleBounds
            assertTrue(connect.top >= 0 && connect.bottom <= device.displayHeight)
            assertTrue(device.hasObject(By.text("Settings")))
            assertTrue(device.hasObject(By.text("TUNNEL TRAFFIC")))
            assertTrue(device.hasObject(By.desc("Traffic over 60 seconds; shared upload and download scale")))
            assertEquals(R.drawable.ic_notification,Surfaces.notification(context).smallIcon.resId)
            device.takeScreenshot(java.io.File(context.filesDir,"home-ui.png"))
        }
    }
}
