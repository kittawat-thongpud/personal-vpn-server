# Android recovery implementation, Client 0.7.2

This implements the first Android phase of `client/PERFORMANCE-REVIEW.md`.
Server configuration, Windows runtime, TUN stack/MTU, app-rule semantics and
production DNS resolver selection are unchanged. P2P development is paused.

- One desired session has a supervised recovery worker. An unexpected exception,
  return or child cancellation is retried. Intentional Stop, consent revocation
  and service destruction cancel ownership. Invalid configuration enters an
  action-required state. Authentication/pin failures retry after 30 seconds or a
  new network, so a transient failure cannot permanently stop recovery; incorrect
  credentials/clock still need correction. There is no tight authentication loop.
- Physical network candidates come from copied callback capabilities and link
  properties. Lost networks cannot be resurrected by late property callbacks.
  A validated usable network outranks stale Wi-Fi. Address/route/DNS/MTU and
  validation/suspension changes invalidate old results; metered-only updates
  do not restart the transport. A short debounce coalesces transition events.
- Every attempt owns a network generation and a cancellable child job. Results
  commit atomically with the generation check. New connectivity cancels old
  endpoint DNS and HTTP work and resets retry backoff. No worker/core is created
  solely by an unconsumed wake event from a previous network.
- Actual path changes replace the transport once and retain capture. This is
  deliberate transport replacement, not seamless migration of old TCP streams.
  The previous interface-notification-plus-explicit-reset combination is gone.
- TUN underlying-network metadata is set on establishment and refreshed when
  it changes, including an empty set during loss. Service-generation ownership
  and a shared lifecycle mutex prevent old service cleanup from overriding a
  newly created session.
- Native probe contexts are registered before I/O. Cancellation covers connect,
  TLS, response headers/body and the captured-path resolver. The core detaches
  before Close, cancels and joins outstanding probes, then releases its native
  instance. Atomic byte counters do not wait behind network requests.
- Selected-outbound proof and OS-capture proof are distinct. The latter uses
  normal OS sockets without Protect or an outbound detour and resolves through
  the TUN DNS address. Both require HTTPS and a valid IP response, with matching
  exit IPs. Fixture trust is available only to the existing instrumentation path.
- Primary endpoint failure tries a second target. One failed cycle keeps the
  core; two consecutive failed cycles enter transport retry. Network-change
  cancellations do not count as health failures. Successful verification clears
  the failure count. TLS pins and REALITY authentication remain enforced.
- Fixed-category backend logs retain the existing 1,000-record / 1 MiB RAM bound.
  Generation, phase, elapsed time and transport type are safe diagnostic fields;
  URLs, local addresses, tokens and native exception text are not production logs.

Qualification includes deterministic supervisor/network-choice tests, native
concurrency tests under the Go race detector, cancelled real QUIC/TLS response
bodies, and Android emulator health-failure/connectivity transitions for both
protocols. Publication requires the complete Windows x86/x64 and Android checks.
Exact completed results belong in `client/VALIDATION.md` after the gates finish.

Physical Redmi Wi-Fi/cellular, captive portal, prolonged screen-off and OEM
process-kill behavior still need real-device acceptance. There is no claim of a
measured throughput improvement or a fix for YouTube/ChatGPT partial failures.

Android contracts: [network callbacks](https://developer.android.com/develop/connectivity/network-ops/reading-network-state),
[cancellable DNS](https://developer.android.com/reference/android/net/DnsResolver),
[underlying networks](https://developer.android.com/reference/android/net/VpnService#setUnderlyingNetworks(android.net.Network[])).
