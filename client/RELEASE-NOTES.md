Client 0.7.2 improves Android VPN recovery after network loss and Wi-Fi/cellular changes.

The service supervises recovery workers and binds attempts to a network generation.
Physical-network callbacks use their supplied snapshots, endpoint DNS is cancellable,
and results from an old network cannot mark the current connection Connected.
Network restoration wakes recovery without a user Reconnect command. Real path
changes replace the transport once while retaining the original Android TUN.

HTTPS probes are cancellable and no longer hold the lock used by traffic counters
or core lifetime control. Initial authentication and separate OS-captured HTTPS
must still agree. A failed primary check tries an independent secondary endpoint;
one failed verification cycle retains the core, and consecutive failures trigger
bounded retries. Profile/authentication errors remain visible for user action.
There is no automatic Direct or protocol fallback and certificate checks remain.

The native captured-path probe uses ordinary, unprotected OS sockets, including
DNS through the TUN, independently of the selected outbound. Native tests cover
probe cancellation over real QUIC/TLS, responsive counters during blocked probes,
and safe concurrent close. Android tests exercise health-target outages, repeated
verification failure, network loss/restoration and retained capture, alongside
the existing protocol, separate-UID policy, TLS, UI and cleanup checks.

Logs remain backend-only, session-memory-only and limited to 1,000 records / 1 MiB.
Network events identify generation and transport type without credentials, local
addresses, browsing destinations or raw native errors.

This shared stable release includes Android and Windows x86/x64 installers under
one version. Windows runtime behavior is unchanged and requalified. No VPS update
or profile regeneration is required. Install over the existing APK to retain
profiles and rules. Physical Redmi radio transitions, China-network performance
and OEM power management remain separate device acceptance checks; no throughput
gain or preservation of existing TCP sessions across a radio change is promised.

Builds, signing and publication run in GitHub Actions. Qualified binary handoff
uses run-and-source-scoped caches with checksum verification; optional emulator
report uploads cannot override the actual test result. Source snapshots and
published installers remain preserved. P2P development is paused.
