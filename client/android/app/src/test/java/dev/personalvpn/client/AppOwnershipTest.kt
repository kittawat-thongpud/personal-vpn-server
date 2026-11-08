package dev.personalvpn.client

import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class AppOwnershipTest {
    private val packages = listOf("org.example.account", "org.example.services")
    private fun rules(apps: Policy, sites: Policy = Policy(Action.Tunnel)) = JSONObject(EngineConfig.compile(
        Config(listOf(profile()), "fixture", apps = apps, sites = sites), "192.0.2.1"
    )).getJSONObject("route").getJSONArray("rules").objects()
    private fun profile() = Profile(id="fixture", hysteria="""{"server":"192.0.2.1","port":443,"auth":"test","sni":"fixture.local"}""")

    @Test fun sharedUidIsKnownAndPreservesEveryPackage() {
        val owner = AppOwnership.resolve(10001, packages.reversed() + packages.first())
        assertFalse(owner.unresolved); assertEquals(10001, owner.uid); assertEquals(packages, owner.packages)
        assertEquals(listOf("android"), AppOwnership.resolve(1000, listOf("android")).packages)
    }
    @Test fun realLookupFailureStaysUnresolvedAndRejected() {
        for (owner in listOf(AppOwnership.resolve(-1, packages), AppOwnership.resolve(10001, emptyList()), AppOwnership.resolve(-1, emptyList(), "SecurityException"))) {
            assertTrue(owner.unresolved); assertEquals(listOf(AppOwnership.UNRESOLVED), owner.packages)
        }
        val reject = rules(Policy(Action.Tunnel)).first { it.optJSONArray("package_name")?.strings()?.contains(AppOwnership.UNRESOLVED) == true }
        assertEquals("reject", reject.getString("action")); assertFalse(reject.has("outbound"))
    }
    @Test fun privateSiteExceptionsDoNotRejectAnUnlistedSharedUid() {
        val native = rules(Policy(Action.Tunnel, listOf(Rule("org.other.app",Action.Bypass,"App"))), Policy(Action.Tunnel,listOf(Rule("2.2.2.2,10.184.6.32",Action.Bypass,"IP"))))
        val owner = AppOwnership.resolve(10001, packages)
        assertTrue(native.filter { it.has("package_name") }.none { it.getJSONArray("package_name").strings().any { it in owner.packages } })
        assertEquals(listOf("2.2.2.2/32","10.184.6.32/32"),native.first { it.has("ip_cidr") }.getJSONArray("ip_cidr").strings())
    }
    @Test fun sharedUidConflictsAreStrictAndIndependentOfEditOrder() {
        val exceptions=listOf(Rule(packages[0],Action.Bypass,"App"),Rule(packages[1],Action.Block,"App"),Rule("org.example.third",Action.Tunnel,"App"))
        for (order in listOf(exceptions,exceptions.reversed())) {
            val native = rules(Policy(Action.Tunnel, order), Policy(Action.Tunnel,listOf(Rule("example.com",Action.Bypass))))
            val matching=native.filter { it.optJSONArray("package_name")?.strings()?.any { it in packages } == true }
            assertEquals("reject",matching.first().getString("action"))
            val app=native.indexOf(matching.first()); val site=native.indexOfFirst { it.has("domain_suffix") }; assertTrue(app<site)
        }
        val tunnelFirst=AppOwnership.orderedRules(listOf(Rule(packages[0],Action.Bypass,"App"),Rule(packages[1],Action.Tunnel,"App")))
        assertEquals(Action.Tunnel,tunnelFirst.first().action)
    }
    @Test fun ownerDiagnosticsAreBoundedAndResetForANewSession() {
        val notices=OwnerNotices(); val shared=AppOwnership.resolve(10001,packages); val missing=AppOwnership.resolve(-1,emptyList())
        assertEquals("policy.shared_uid",notices.next(shared,0)!!.first)
        assertEquals("policy.owner_unresolved",notices.next(missing,0)!!.first)
        repeat(1000) { assertNull(notices.next(shared,1000)); assertNull(notices.next(missing,1000)) }
        assertNotNull(notices.next(shared,60_000)); assertNotNull(OwnerNotices().next(shared,0))
    }
}
