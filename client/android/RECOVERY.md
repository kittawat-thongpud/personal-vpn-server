# Android recovery implementation

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
  Initial selection prefers a validated usable network. During a session, the
  current nonsuspended path stays selected while it is verified or its bounded
  initial verification is pending. Android validation/metering changes alone do
  not replace that transport. Actual loss/suspension selects an available path
  immediately; address/route/DNS/MTU changes invalidate old results. A short
  debounce coalesces transition events.
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
  core; two consecutive failed cycles reject the current path. An alternative
  nonsuspended underlay is then tried immediately, if available. Failed paths
  have a 30-second cooldown so two failing links cannot continuously replace
  each other. If no eligible alternative exists, the normal bounded transport
  retry remains active on the retained path. New physical loss/suspension can
  select even a recently failed path when it is the only available link. Network-change
  cancellations do not count as health failures. Successful verification clears
  the failure count. TLS pins and REALITY authentication remain enforced.
- Fixed-category backend logs retain the existing 1,000-record / 1 MiB RAM bound.
  Generation, phase, elapsed time and transport type are safe diagnostic fields;
  URLs, local addresses, tokens and native exception text are not production logs.
  `network.selection` reports fixed reasons such as `retain_verified`,
  `retain_pending`, `health_alternative` and `failed_paths_cooldown`.
  `backend.retry_woken` distinguishes a new generation from same-path validation
  returning during transport backoff.
  `backend.phase` records `network_recovery` through verified outbound and
  captured HTTPS for the final generation. Service destruction attempts capture
  release even if native core teardown throws, then cancels its scope.

Qualification includes deterministic supervisor/network-choice tests, native
concurrency tests under the Go race detector, cancelled real QUIC/TLS response
bodies, and Android emulator health-failure/connectivity transitions for both
protocols. Publication requires the complete Windows x86/x64 and Android checks.
Exact completed results belong in `client/VALIDATION.md` after the gates finish.

Local hardening verification compiled the actual recovery policy and its tests
with Kotlin 2.1.20, coroutines 1.10.2 and JUnit 4.13.2: 15 tests passed. The added
cases cover repeated validation flips, bounded pending selection, alternate
selection and cooldown after health rejection, true loss/suspension, path
changes, and capture release after native teardown failure. A coroutine case
also verifies that destruction waits for the worker's lifecycle lock before
releasing capture and cancelling the scope. This is not a full APK build,
emulator handover result or physical-device qualification of these changes.

Physical Redmi Wi-Fi/cellular, captive portal, prolonged screen-off and OEM
process-kill behavior still need real-device acceptance. There is no claim of a
measured throughput improvement or a fix for YouTube/ChatGPT partial failures.

## Physical-device acceptance

Install the signed candidate APK over the existing app to retain profiles and rules.
Keep the Windows connection available while testing the phone. For each protocol:

1. Connect, confirm the VPS exit IP and send traffic from another app.
2. Change Wi-Fi to mobile data and back without pressing Reconnect. Confirm the
   status leaves Waiting/Reconnecting and fresh traffic works on both networks.
3. Remove both upstreams for 10-30 seconds and restore one. The desired session
   must recover automatically; an old exit-IP result must not imply Connected.
4. Repeat with the screen off. Record any OEM battery restriction separately.
5. Disconnect deliberately, then change networks: the app must stay disconnected.

If recovery stalls, export the final backend logs before pressing Reconnect or
exiting the process. Include the Home status, protocol and approximate handover
time. Logs are session-memory-only, so process exit removes that evidence.

Android contracts: [network callbacks](https://developer.android.com/develop/connectivity/network-ops/reading-network-state),
[cancellable DNS](https://developer.android.com/reference/android/net/DnsResolver),
[underlying networks](https://developer.android.com/reference/android/net/VpnService#setUnderlyingNetworks(android.net.Network[])).
