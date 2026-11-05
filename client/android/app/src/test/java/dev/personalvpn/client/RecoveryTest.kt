package dev.personalvpn.client

import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.*
import org.junit.Test

class RecoveryTest {
    @Test fun supervisorRecoversFailureUnexpectedCompletionAndChildCancellation() = runBlocking {
        var desired = true; var runs = 0; val failed = mutableListOf<Int>()
        superviseRecovery({ desired }, {
            when (++runs) { 1 -> error("worker failure"); 2 -> Unit; 3 -> throw CancellationException("child ended"); else -> desired = false }
        }, { attempt, _ -> failed += attempt }, { yield() })
        assertEquals(4, runs); assertEquals(listOf(1, 2, 3), failed)
    }
    @Test fun explicitStopDoesNotRestartAndBlockedConfigDoesNotSpin() = runBlocking {
        var runs = 0
        val worker = launch { superviseRecovery({ true }, { runs++; awaitCancellation() }, { _, _ -> error("restart after stop") }, {}) }
        yield(); worker.cancelAndJoin(); assertEquals(1, runs)
        try { superviseRecovery({ true }, { throw SessionBlocked("configuration") }, { _, _ -> error("blocked retry") }, {}); fail("Expected blocked session") }
        catch (blocked: SessionBlocked) { assertEquals("configuration", blocked.category) }
    }
    @Test fun validatedCellularWinsOverStaleWifiAndMetadataDoesNotResetTransport() {
        val wifi = NetworkChoice("wifi", "wifi/path", false, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        assertEquals(mobile, chooseNetwork(listOf(wifi, mobile), wifi.id))
        assertEquals(wifi, chooseNetwork(listOf(wifi, mobile.copy(suspended = true)), mobile.id))
        assertEquals(wifi.generationKey, wifi.copy(metered = true).generationKey)
        assertNotEquals(wifi.generationKey, wifi.copy(path = "wifi/new-address").generationKey)
        assertNotEquals(wifi.generationKey, wifi.copy(validated = true).generationKey)
        assertEquals(null, chooseNetwork(emptyList(), wifi.id))
    }
    @Test fun oneFailedVerificationDoesNotRestartAndSuccessClearsMisses() {
        val failures = HealthFailures()
        assertFalse(failures.failed()); failures.success(); assertFalse(failures.failed()); assertTrue(failures.failed())
    }
    @Test fun nativeFailureIsCategorizedWithoutCredentials() {
        val secret = "private-profile-credential"
        assertEquals("reality_authentication", BackendFailures.reason(Exception("Get https://$secret/: reality verification failed")))
        assertEquals("connection_closed", BackendFailures.reason(Exception("Get https://$secret/: EOF")))
        assertEquals("certificate_validation", BackendFailures.reason(Exception("wrapped", Exception("x509: certificate signed by unknown authority"))))
        assertEquals("backend_error", BackendFailures.reason(Exception(secret)))
        assertEquals("exit_mismatch", BackendFailures.reason(Exception("Captured exit differs from authenticated outbound")))
        assertEquals("http_status", BackendFailures.reason(Exception("health HTTP status 503")))
    }
    @Test fun staleCallbackDoesNotSkipBackoff() = runBlocking {
        val events = Channel<Unit>(Channel.CONFLATED)
        events.trySend(Unit)
        val waiting = async { awaitNetworkChange(events, 200) { false } }
        delay(50)
        assertFalse("The old callback must not finish the wait", waiting.isCompleted)
        waiting.await()
    }
    @Test fun newNetworkWakesBackoffAndCancellationIsPrompt() = runBlocking {
        val events = Channel<Unit>(Channel.CONFLATED)
        var changed = false
        val waiting = async { awaitNetworkChange(events, 30000) { changed } }
        yield(); changed = true; events.trySend(Unit)
        withTimeout(1000) { waiting.await() }
        val cancelled = launch { awaitNetworkChange(events, 30000) { false } }
        yield(); cancelled.cancelAndJoin()
        assertTrue(cancelled.isCancelled)
    }
}
