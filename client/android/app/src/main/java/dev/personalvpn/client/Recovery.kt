package dev.personalvpn.client

import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.*
import kotlinx.coroutines.withTimeoutOrNull

// A queued callback from the old network must not consume the retry backoff.
// A genuine change may wake it immediately; cancellation always remains prompt.
internal suspend fun awaitNetworkChange(events: ReceiveChannel<Unit>, timeoutMs: Long, changed: () -> Boolean) {
    withTimeoutOrNull(timeoutMs) { while (!changed()) events.receive() }
}

internal class SessionBlocked(val category: String) : Exception(category)
internal class StaleNetwork : CancellationException("Network generation changed")

// The owner cancels this supervisor on Disconnect/revoke. Unexpected worker
// failure or completion cannot silently leave a desired session without a worker.
internal suspend fun superviseRecovery(desired: () -> Boolean, run: suspend () -> Unit,
    failed: (Int, Exception) -> Unit, wait: suspend (Int) -> Unit) {
    var failures = 0
    while (currentCoroutineContext().isActive && desired()) {
        try {
            run()
            if (!desired()) return
            throw IllegalStateException("Recovery worker completed unexpectedly")
        } catch (cancel: CancellationException) {
            currentCoroutineContext().ensureActive()
            if (!desired()) return
            failures++; failed(failures, IllegalStateException("Recovery worker cancelled unexpectedly", cancel)); wait(failures)
        }
        catch (blocked: SessionBlocked) { throw blocked }
        catch (error: Exception) { failures++; failed(failures, error); wait(failures) }
    }
}

internal data class NetworkChoice(val id: String, val path: String, val validated: Boolean,
    val suspended: Boolean, val wifi: Boolean, val metered: Boolean) {
    val generationKey get() = "$path/$validated/$suspended"
    val score get() = (if (validated) 100 else 0) + (if (!suspended) 20 else 0) + (if (wifi) 2 else 1)
}
internal fun chooseNetwork(values: List<NetworkChoice>, current: String?): NetworkChoice? =
    values.sortedWith(compareByDescending<NetworkChoice> { it.score }.thenByDescending { it.id == current }.thenBy { it.id }).firstOrNull()

internal class HealthFailures(private val limit: Int = 2) {
    var count = 0; private set
    fun success() { count = 0 }
    fun failed(): Boolean { count++; return count >= limit }
}

internal object BackendFailures {
    fun reason(error: Throwable): String {
        val text = generateSequence(error) { it.cause?.takeIf { cause -> cause !== it } }.take(8)
            .joinToString(" ") { it.message.orEmpty().take(2048) }.lowercase()
        // Only return fixed categories. Native messages may contain URLs,
        // authentication material or user destinations and are never copied.
        return when {
            "captured exit differs" in text -> "exit_mismatch"
            "health http status" in text -> "http_status"
            "health response invalid" in text -> "health_response"
            "reality verification failed" in text -> "reality_authentication"
            "pin mismatch" in text -> "certificate_pin"
            "certificate" in text || "x509" in text -> "certificate_validation"
            "timeout" in text || "timed out" in text || "deadline exceeded" in text -> "timeout"
            "no such host" in text || "unknownhost" in text || "name resolution" in text -> "dns"
            "connection reset" in text || "forcibly closed" in text -> "connection_reset"
            "connection refused" in text -> "connection_refused"
            "eof" in text || "unexpected end" in text -> "connection_closed"
            "authentication" in text || "unauthorized" in text || "invalid password" in text -> "authentication"
            "tls" in text || "handshake" in text -> "tls_handshake"
            "network" in text || "connect" in text -> "network"
            else -> "backend_error"
        }
    }
}
