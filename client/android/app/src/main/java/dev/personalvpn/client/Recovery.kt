package dev.personalvpn.client

import kotlinx.coroutines.channels.ReceiveChannel
import kotlinx.coroutines.withTimeoutOrNull

// A queued callback from the old network must not consume the retry backoff.
// A genuine change may wake it immediately; cancellation always remains prompt.
internal suspend fun awaitNetworkChange(events: ReceiveChannel<Unit>, timeoutMs: Long, changed: () -> Boolean) {
    withTimeoutOrNull(timeoutMs) { while (!changed()) events.receive() }
}

internal object BackendFailures {
    fun reason(error: Throwable): String {
        val text = generateSequence(error) { it.cause?.takeIf { cause -> cause !== it } }.take(8)
            .joinToString(" ") { it.message.orEmpty().take(2048) }.lowercase()
        // Only return fixed categories. Native messages may contain URLs,
        // authentication material or user destinations and are never copied.
        return when {
            "reality verification failed" in text -> "reality_authentication"
            "pin mismatch" in text -> "certificate_pin"
            "certificate" in text || "x509" in text -> "certificate_validation"
            "timeout" in text || "timed out" in text || "deadline exceeded" in text -> "timeout"
            "no such host" in text || "unknownhost" in text || "name resolution" in text -> "dns"
            "connection reset" in text || "forcibly closed" in text -> "connection_reset"
            "connection refused" in text -> "connection_refused"
            "eof" in text || "unexpected end" in text -> "connection_closed"
            "auth" in text || "unauthorized" in text -> "authentication"
            "tls" in text || "handshake" in text -> "tls_handshake"
            "network" in text || "connect" in text -> "network"
            "captured exit differs" in text -> "exit_mismatch"
            else -> "backend_error"
        }
    }
}
