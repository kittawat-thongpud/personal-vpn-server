package dev.personalvpn.client

import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.junit.Assert.*
import org.junit.Test

class RecoveryTest {
    @Test fun nativeFailureIsCategorizedWithoutCredentials() {
        val secret = "private-profile-credential"
        assertEquals("reality_authentication", BackendFailures.reason(Exception("Get https://$secret/: reality verification failed")))
        assertEquals("connection_closed", BackendFailures.reason(Exception("Get https://$secret/: EOF")))
        assertEquals("certificate_validation", BackendFailures.reason(Exception("wrapped", Exception("x509: certificate signed by unknown authority"))))
        assertEquals("backend_error", BackendFailures.reason(Exception(secret)))
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
