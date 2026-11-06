package dev.personalvpn.client
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class PolicyTest {
    @Test fun namedGroupsCompileAllMembersAndPreserveLegacySettings() {
        val p=profile()
        val groups=Policy(Action.Tunnel,listOf(Rule("2.2.2.*,10.46.136.6,2.2.2.2",Action.Bypass,"IP","Private lab"),Rule("www.google.com,www.youtube.com",Action.Block,"Domain","Web services")))
        val config=Config(listOf(p),p.id,sites=groups)
        val native=JSONObject(EngineConfig.compile(config,"192.0.2.1"))
        val rules=native.getJSONObject("route").getJSONArray("rules").objects()
        assertEquals(listOf("2.2.2.0/24","10.46.136.6/32","2.2.2.2/32"),rules.first { it.has("ip_cidr") }.getJSONArray("ip_cidr").strings())
        assertEquals(listOf("www.google.com","www.youtube.com"),rules.first { it.has("domain_suffix") }.getJSONArray("domain_suffix").strings())
        assertEquals(config,Config.parse(config.json().toString()))
        val old=Policy.parse(JSONObject("""{"mode":"Tunnel","rules":[{"target":"10.46.136.6","action":"Bypass","kind":"IP"}]}"""))
        assertEquals("10.46.136.6",old.rules.single().displayName)
        assertEquals("",old.rules.single().name)
        assertFalse(native.toString().contains("Private lab"))
        assertEquals("Example.com, www.youtube.com",SiteTargets.clean("Domain","Example.com\nexample.com;www.youtube.com"))
        assertEquals("2001:db8:0:0:0:0:0:0/64",SiteTargets.normalize("IP","2001:db8::9/64"))
        assertEquals("10.46.136.6/32",SiteTargets.normalize("IP","010.046.136.006"))
        assertEquals("10.0.0.0/8",SiteTargets.normalize("IP","10.*.*.*"))
    }
    @Test fun invalidOrDuplicateSiteMembersCannotBeSaved() {
        fun rejected(check: ()->Unit) { try { check();fail("Invalid target accepted") } catch(_: IllegalArgumentException) { } }
        listOf("2.*.3.*","256.2.2.*","1.2.3.4/33","1.2.3","::g","1.2.3.4%eth0").forEach { bad -> rejected { SiteTargets.parse("IP",bad) } }
        rejected { SiteTargets.parse("Domain","www.google.com,1.2.3.4") }
        rejected { SiteTargets.parse("Domain","https://example.com") }
        rejected { SiteTargets.parse("Domain",(0..500).joinToString(","){"site$it.example"}) }
        rejected { SiteTargets.validate(Policy(rules=listOf(Rule((0..249).joinToString(","){"first$it.example"},Action.Tunnel),Rule((0..250).joinToString(","){"second$it.example"},Action.Bypass)))) }
        rejected { SiteTargets.validate(Policy(rules=listOf(Rule("2.2.2.*",Action.Bypass,"IP"),Rule("2.2.2.1/24",Action.Tunnel,"IP")))) }
    }
    @Test fun bulkAppActionsReplaceSelectedAndPreserveOtherExceptions() {
        val original = Policy(Action.Bypass, listOf(Rule("org.example.first", Action.Tunnel, "App"), Rule("org.example.kept", Action.Block, "App")))
        val updated = AppRules.apply(original, setOf("org.example.first", "org.example.second"), Action.Block)
        assertEquals(Action.Bypass, updated.mode)
        assertEquals(3, updated.rules.size)
        assertTrue(updated.rules.all { it.action == Action.Block })
        val replaced = AppRules.apply(updated, setOf("org.example.first", "org.example.second"), Action.Bypass)
        assertEquals(3, replaced.rules.size)
        assertEquals(Action.Block, replaced.rules.single { it.target == "org.example.kept" }.action)
        val removed = AppRules.remove(replaced, setOf("org.example.first", "org.example.second"))
        assertEquals(listOf(Rule("org.example.kept", Action.Block, "App")), removed.rules)
        assertEquals(Action.Bypass, removed.mode)
        assertEquals(Action.Tunnel, original.rules.first().action)
    }
    @Test(expected = IllegalArgumentException::class) fun bulkSelectionCannotOverrideOwnControlPackage() { AppRules.apply(Policy(), setOf("org.example.app", EngineConfig.APP), Action.Block) }
    @Test(expected = IllegalArgumentException::class) fun bulkSelectionCannotStoreRulesOffAsAnAction() { AppRules.apply(Policy(), setOf("org.example.app"), Action.Disabled) }
    @Test fun androidFrameworkPackageIsAValidSystemAppException() {
        val policy = AppRules.apply(Policy(Action.Tunnel), setOf("android"), Action.Block)
        val p = profile()
        EngineConfig.compile(Config(listOf(p), p.id, apps = policy), "192.0.2.1")
        assertEquals(listOf(Rule("android", Action.Block, "App")), policy.rules)
    }
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
