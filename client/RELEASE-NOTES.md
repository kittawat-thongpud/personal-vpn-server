Client 0.7.1 fixes Android REALITY interoperability with modern Xray servers.

The pinned sing-box client's REALITY handshake removed the browser's hybrid
X25519MLKEM768 key share. Xray 26.9.8 and newer reject that ClientHello, producing
"reality verification failed" even when the same profile works with an Xray
client. The Android backport retains the original browser key shares and uses
the corresponding X25519 authentication key. Server credentials and TLS
verification remain required; there is no automatic Direct fallback.

Qualification now uses official Xray 26.10.31, the same version as the VPS,
instead of sing-box at both ends of the REALITY emulator fixture. Native checks
reproduce the unpatched failure, verify repeated HTTPS with the corrected
handshake and reject an incorrect short ID. Android TUN capture and App rules
are exercised against the Xray fixture too.

Backend logs identify the failure stage and a safe error category rather than
only "proxyerror". Retry backoff ignores stale network callbacks while a genuine
Wi-Fi/cellular change can still wake recovery. Logs remain session-only and
bounded; credentials and raw native error messages are not logged.

This shared stable Latest release includes the signed Android APK and Windows
x86/x64 installers and portable ZIPs. Windows functionality is unchanged and
requalified under the same client version. Builds and packaging run in GitHub
Actions. Physical Redmi use, radio handover and OEM battery policy still need
device acceptance; emulator qualification is recorded separately. Update the
existing APK to preserve profiles and rules. No VPS reinstallation is required.
