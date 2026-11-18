package dev.personalvpn.client

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class SiteTunnelProfileTest {
    private fun fixture() = requireNotNull(javaClass.getResourceAsStream("/site-tunnel-v1.json")).use { it.readBytes() }

    @Test fun sharedDocumentRoundTripsWithNamesModesAndRoutes() {
        val policy = SiteTunnelProfile.read(fixture().inputStream())
        assertEquals(Action.Bypass, policy.mode)
        assertEquals(listOf(Action.Tunnel, Action.Block, Action.Bypass), policy.rules.map { it.action })
        assertEquals("รายการเว็บ", policy.rules.first().name)
        assertEquals(listOf("10.46.136.6/32", "192.0.2.0/24", "2001:db8:0:0:0:0:0:0/32"), SiteTargets.parse("IP", policy.rules[1].target))
        assertEquals(policy, SiteTunnelProfile.read(SiteTunnelProfile.export(policy).inputStream()))
    }

    @Test fun invalidAndUnrelatedProfilesAreRejected() {
        for (input in listOf("{}", "{\"profiles\":[]}", fixture().toString(Charsets.UTF_8).replace("\"version\": 1", "\"version\": 2"),
            JSONObject(fixture().toString(Charsets.UTF_8)).apply { getJSONArray("rules").put(getJSONArray("rules").getJSONObject(0)) }.toString())) {
            assertThrows(IllegalArgumentException::class.java) { SiteTunnelProfile.read(input.byteInputStream()) }
        }
        assertThrows(IllegalArgumentException::class.java) { SiteTunnelProfile.read(ByteArray(512 * 1024 + 1).inputStream()) }
    }
}
