# Android validation

The shared **0.7.2** release adds supervised recovery, network generations,
cancellable DNS/HTTPS probes and multi-target health hysteresis. Android 14
instrumentation passed `OK (4 tests)` in 160.830 s, including actual emulator
network loss, cellular restoration and return to Wi-Fi without a manual
Reconnect, retaining the same TUN for both protocols. JVM tests, lint, native
race/cancellation/protocol tests and Windows x86/x64 qualification passed too.
The APK retains the existing signing identity and profiles can be kept by
installing over the previous app. Physical Redmi acceptance remains pending.
Downloaded APK SHA-256:
`3da4202995cfed3b4514c312e42ac0c8bd14b7ac804bb856f7eb8dd3f550013d`.

The **0.7.1** correction uses modern Xray for REALITY qualification and
fixes the hybrid ClientHello compatibility gap found after 0.7.0. See
[shared qualification](../VALIDATION.md) for native regression, live VPS evidence
and the all-platform release gates. The installed-app multiselection UI remains.
The 0.1.0 results below are retained as historical evidence.

Android 0.1.0 preview was published on 2026-11-03 from commit
`631043c33f5d6018eb5530810e6bb43b853c58c3`.

- [Completed qualification and release run](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41612467476): native, app, emulator and release jobs succeeded.
- [Private signed APK release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/android-v0.1.0): APK, INSTALL.md, SIGNATURE.txt and SHA256SUMS.txt are uploaded; this is a prerelease, not a draft.
- APK SHA-256: `2ff79f8d2b62b01d07bec96c4b9d9a8e5f753666717b8dfe24b664bc7e47b1ba`.
- Downloaded APK checksum independently matched the release asset. APK Signature Scheme v2 verification succeeded and the RSA-3072 certificate matched SIGNING-CERTIFICATE.txt. Both arm64-v8a and x86_64 libraries are present.

## Completed checks

JVM policy/import/log tests and Android lint passed. The native qualification
includes real TLS pin-match/pin-mismatch handshakes and eight successive
authenticated HTTPS requests over a local Hysteria 2 gateway. A short-response
EOF found during qualification was fixed by consuming the health response before
closing its HTTP transport.

Android 14 x86_64 emulator instrumentation reported `OK (3 tests)` in 80.223 s:

- Android Keystore encrypted storage round-trip and native configuration validation.
- Actual Hysteria 2 and REALITY/Vision TUN capture; authenticated native HTTPS and separately captured Android HTTPS agree.
- A companion APK with a different UID successfully sends HTTPS and UDP through the gateway.
- App Block rejects that companion; explicit App Tunnel wins over Site Block; explicit App Bypass reaches the target through a protected Direct socket.
- Reconnects and protocol changes retain the same Android TUN name/index. Normal Disconnect releases capture, clears Exit IP and shows zero current throughput.
- Tunnel byte counters increase with captured traffic. Arbitrary exported Activity commands cannot disconnect; the authorized widget PendingIntent command can disconnect when foreground launch is permitted.
- Home controls fit on screen. The resulting Home screenshot was visually inspected.

The emulator uses generated credentials and an isolated TLS/DNS/UDP gateway on
the GitHub runner. It does not use the user's VPS credentials or prove Internet
access from the physical phone in China. Notification/widget/tile UI interactions,
radio handover and OEM background behavior still require physical acceptance.

The target Redmi Note 13 Pro+ 5G / Android 14 is not connected through ADB in
this workspace. Physical throughput, Wi-Fi/cellular transitions, screen-off and
OEM battery policies are not claimed as tested.
