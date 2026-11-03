package dev.personalvpn.client
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PolicyTest {
    private fun profile() = Profile(hysteria = """{"server":"192.0.2.1","port":443,"auth":"synthetic","sni":"fixture.local","pin":"${"ab".repeat(32)}","obfs":""}""")
    @Test fun appExceptionWinsSiteAndDefaults() {
        val p = profile(); val c = Config(listOf(p), p.id, apps = Policy(Action.Block, listOf(Rule("org.example.app", Action.Bypass, "App"))), sites = Policy(Action.Tunnel, listOf(Rule("example.com", Action.Block))))
        val config = JSONObject(EngineConfig.compile(c, "192.0.2.1")); val rules = config.getJSONObject("route").getJSONArray("rules").objects()
        val appIndex = rules.indexOfFirst { it.optJSONArray("package_name")?.strings()?.contains("org.example.app") == true }
        val siteIndex = rules.indexOfFirst { it.has("domain_suffix") }
        assertTrue(appIndex >= 0 && appIndex < siteIndex)
        assertEquals("direct", rules[appIndex].getString("outbound"))
        assertEquals("reject", rules.last().getString("action"))
        assertEquals("ipv4_only", config.getJSONObject("dns").getString("strategy"))
    }
    @Test fun rulesOffRetainsButIgnoresExceptions() {
        val p = profile(); val c = Config(listOf(p), p.id, apps = Policy(Action.Disabled, listOf(Rule("org.example.app", Action.Block, "App"))), sites = Policy(Action.Disabled, listOf(Rule("example.com", Action.Block))))
        val config = JSONObject(EngineConfig.compile(c, "192.0.2.1"))
        assertEquals(3, config.getJSONObject("route").getJSONArray("rules").length())
        assertEquals("tunnel", config.getJSONObject("route").getString("final"))
    }
    @Test fun fullCertificatePinIsPreserved() {
        val out = EngineConfig.outbound(profile(), "Hysteria 2", "192.0.2.1")
        assertEquals(32, java.util.Base64.getDecoder().decode(out.getJSONObject("tls").getJSONArray("certificate_sha256").getString(0)).size)
        assertFalse(out.getJSONObject("tls").optBoolean("insecure"))
    }
    @Test(expected = IllegalArgumentException::class) fun insecureWithoutPinRejected() { Imports.parse(listOf("""{"server":"192.0.2.1","auth":"secret","tls":{"sni":"fixture.local","insecure":true}}""")) }
    @Test fun importsBothVpnctlExports() {
        val p = Imports.parse(listOf("""{"server":"192.0.2.1:443","auth":"user:password","tls":{"sni":"fixture.local","insecure":true,"pinSHA256":"${"ab".repeat(32)}"}}"""))
        assertEquals("192.0.2.1", p.single().endpoint("Hysteria 2"))
        assertEquals("user:password", JSONObject(p.single().hysteria!!).getString("auth"))
    }
    @Test fun encryptedModelRoundTripPreservesRules() { val p = profile(); val c = Config(listOf(p), p.id, apps = Policy(Action.Bypass, listOf(Rule("org.example.app", Action.Block, "App")))); assertEquals(c, Config.parse(c.json().toString())) }
    @Test fun logsAreBoundedEvenWithLongMultibyteResults() { val ring = EventRing(); var records = emptyList<BackendEvent>(); repeat(2500) { records = ring.add("WARN", "backend.retry", "ผล".repeat(1000)) }; assertTrue(records.size <= 1000); assertTrue(records.sumOf { it.detail.toByteArray().size + 200 } < 1200 * 1024); ring.clear(); assertEquals(1, ring.add("INFO", "state.connected", "").size) }
}
