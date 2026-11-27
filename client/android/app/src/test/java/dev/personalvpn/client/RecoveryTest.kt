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
    @Test fun transportFailureAfterStopDoesNotReportRetry() = runBlocking {
        var desired = true
        superviseRecovery({ desired }, {
            desired = false
            throw java.io.IOException("Socket closed when VPN permission was revoked")
        }, { _, _ -> fail("The stopped session reported a retry") }, { fail("The stopped session entered backoff") })
    }
    @Test fun validatedCellularWinsOverStaleWifiAndMetadataDoesNotResetTransport() {
        val wifi = NetworkChoice("wifi", "wifi/path", false, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        assertEquals(mobile, chooseNetwork(listOf(wifi, mobile), wifi.id))
        assertEquals(wifi, chooseNetwork(listOf(wifi, mobile.copy(suspended = true)), mobile.id))
        assertEquals(wifi.generationKey, wifi.copy(metered = true).generationKey)
        assertNotEquals(wifi.generationKey, wifi.copy(path = "wifi/new-address").generationKey)
        assertEquals(wifi.generationKey, wifi.copy(validated = true).generationKey)
        assertNotEquals(wifi.generationKey, wifi.copy(suspended = true).generationKey)
        assertEquals(null, chooseNetwork(emptyList(), wifi.id))
    }
    @Test fun oneFailedVerificationDoesNotRestartAndSuccessClearsMisses() {
        val failures = HealthFailures()
        assertFalse(failures.failed()); failures.success(); assertFalse(failures.failed()); assertTrue(failures.failed())
    }
    @Test fun healthyAndPendingPathsSurviveValidationOscillation() {
        val wifi = NetworkChoice("wifi", "wifi/path", true, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        val selector = UnderlaySelector()
        selector.verified(wifi)
        var selected = wifi
        repeat(8) { round ->
            val updated = wifi.copy(validated = round % 2 == 0)
            val decision = selector.select(listOf(updated, mobile), selected, round.toLong())
            assertEquals(updated, decision.choice)
            assertEquals("retain_verified", decision.reason)
            selected = decision.choice!!
        }
        selector.reset()
        val pending = selector.select(listOf(wifi.copy(validated = false), mobile), wifi, 10)
        assertEquals(wifi.id, pending.choice?.id)
        assertEquals("retain_pending", pending.reason)
        val inactive = selector.select(listOf(wifi.copy(validated = false), mobile), wifi, 11, retainCurrent = false)
        assertEquals("Before a session starts, prefer the validated network", mobile, inactive.choice)
    }
    @Test fun rejectedPathMovesToAlternativeWithoutBouncingBetweenFailedPaths() {
        val wifi = NetworkChoice("wifi", "wifi/path", true, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        val selector = UnderlaySelector()
        selector.verified(wifi)
        selector.failed(wifi, 100)
        val replacement = selector.select(listOf(wifi, mobile), wifi, 100)
        assertEquals(mobile, replacement.choice)
        assertEquals("health_alternative", replacement.reason)
        selector.failed(mobile, 200)
        val retained = selector.select(listOf(wifi, mobile), mobile, 200)
        assertEquals(mobile, retained.choice)
        assertEquals("failed_paths_cooldown", retained.reason)
        val eligibleAgain = selector.select(listOf(wifi, mobile), mobile, 30101)
        assertEquals(wifi, eligibleAgain.choice)
        selector.verified(wifi)
        assertEquals("retain_verified", selector.select(listOf(wifi, mobile), wifi, 30102).reason)
    }
    @Test fun trueLossSuspensionAndPathChangesRemainImmediate() {
        val wifi = NetworkChoice("wifi", "wifi/path", true, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        val selector = UnderlaySelector()
        selector.verified(wifi)
        assertEquals("underlay_lost", selector.select(listOf(mobile), wifi, 1).reason)
        val suspended = selector.select(listOf(wifi.copy(suspended = true), mobile), wifi, 2)
        assertEquals(mobile, suspended.choice)
        assertEquals("underlay_suspended", suspended.reason)
        val changed = wifi.copy(path = "wifi/new-address")
        assertEquals("path_changed", selector.select(listOf(changed), wifi, 3).reason)
        assertEquals("retain_pending", selector.select(listOf(changed), changed, 4).reason)
        assertEquals("no_underlay", selector.select(emptyList(), changed, 5).reason)
    }
    @Test fun freshVerifiedHealthClearsThePathPenaltyAndLossIgnoresCooldown() {
        val wifi = NetworkChoice("wifi", "wifi/path", true, false, true, false)
        val mobile = NetworkChoice("mobile", "mobile/path", true, false, false, true)
        val selector = UnderlaySelector()
        selector.failed(wifi, 0)
        selector.verified(wifi)
        assertEquals("retain_verified", selector.select(listOf(wifi, mobile), wifi, 1).reason)
        selector.failed(mobile, 2)
        val lost = selector.select(listOf(mobile), wifi, 3)
        assertEquals(mobile, lost.choice)
        assertEquals("underlay_lost", lost.reason)
    }
    @Test fun captureIsReleasedWhenNativeTeardownThrows() {
        val calls = mutableListOf<String>()
        val failure = IllegalStateException("native teardown failed")
        try {
            releaseSessionResources({ calls += "core"; throw failure }, { calls += "capture" })
            fail("Teardown error must still be reported")
        } catch (error: IllegalStateException) { assertSame(failure, error) }
        assertEquals(listOf("core", "capture"), calls)
    }
    @Test fun nativeCloseStillRunsAfterCancellationAndCounterErrors() {
        val calls = mutableListOf<String>()
        val cancellationFailure = IllegalStateException("cancel failed")
        val counterFailure = IllegalStateException("counter failed")
        try {
            releaseCoreResources(
                { calls += "cancel"; throw cancellationFailure },
                { calls += "counters"; throw counterFailure },
                { calls += "close" })
            fail("Cleanup errors must remain visible")
        } catch (error: IllegalStateException) {
            assertSame(cancellationFailure, error)
            assertArrayEquals(arrayOf(counterFailure), error.suppressed)
        }
        assertEquals(listOf("cancel", "counters", "close"), calls)
    }
    @Test fun destructionWaitsForWorkerCleanupBeforeReleasingCaptureAndCancellingScope() = runBlocking {
        val lifecycle = kotlinx.coroutines.sync.Mutex()
        val service = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val entered = CompletableDeferred<Unit>()
        val workerReleased = CompletableDeferred<Unit>()
        val calls = mutableListOf<String>()
        val worker = service.launch {
            lifecycle.lock()
            try { entered.complete(Unit); awaitCancellation() }
            finally {
                withContext(NonCancellable) { workerReleased.await(); calls += "worker" }
                lifecycle.unlock()
            }
        }
        entered.await(); worker.cancel()
        val cleanup = service.launch(start = CoroutineStart.UNDISPATCHED) {
            try {
                lifecycle.lock()
                try { releaseSessionResources({ calls += "core" }, { calls += "capture" }) }
                finally { lifecycle.unlock() }
            } finally { service.cancel() }
        }
        assertTrue("Pending cleanup must not cancel its own scope", service.isActive)
        assertFalse(cleanup.isCompleted)
        workerReleased.complete(Unit)
        cleanup.join()
        assertEquals(listOf("worker", "core", "capture"), calls)
        assertFalse(service.isActive)
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
