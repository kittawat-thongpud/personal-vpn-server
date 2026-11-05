# Client 0.7.2 Android recovery — 2026-11-06

Qualified source: `225a4579d94961763f9e2c4c2d40e5d87883adc4`.
[Unified qualification and publication](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42079196341)
completed successfully. The shared
[Client 0.7.2 release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.7.2)
is stable, non-draft and Latest, with all 12 assets: signed Android APK,
Windows x86/x64 installers and portable ZIPs, previews, guides, checksums,
signature and source manifest. `base/client-v0.7.2`, `release/client-v0.7.2`
and the tag resolve to the qualified source; `legacy/client-v0.7.1` preserves
the preceding source without moving its release or installers.

All 12 published assets were downloaded independently. All 11 entries in the
release checksum list and every source-manifest size/hash matched. The APK's
arm64/x86_64 ELF headers and both Windows application PE architectures matched
their package labels; the APK signing report matches the pinned certificate.
APK SHA-256:
`3da4202995cfed3b4514c312e42ac0c8bd14b7ac804bb856f7eb8dd3f550013d`.

The Android recovery worker is supervised while the desired session is active.
Network generation checks discard stale results; actual path changes replace
the transport once while retaining TUN. Probe cancellation and native lifetime
control are separate from byte-counter reads. Authentication/pin failures stay
visible and retry slowly or on a new network. Deliberate Disconnect and consent
revocation stop recovery. Invalid configuration requires user correction.

## Completed qualification

- Android JVM tests and lint passed. APK versionName `0.7.2`, versionCode `70200`,
  arm64-v8a/x86_64 libraries and the unchanged signing certificate were checked.
- The native library is reused only with an exact recipe/source hash. Its
  [native qualification](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42076439785/job/127417854729)
  passed real repeated QUIC/TLS requests, cancellation during a stalled response
  body, certificate pin acceptance/rejection, and patched REALITY against
  official Xray. Wrong REALITY credentials remain rejected. Go race-detector
  tests cover responsive counters during blocked probes and concurrent Close
  cancelling/joining probes before releasing the instance.
- Android 14 emulator instrumentation reported `OK (4 tests)` in **160.830 s**.
  A primary HTTPS target outage retained the core for both protocols. Repeated
  complete verification failures on Hysteria 2 entered Degraded before transport
  retry and recovered without a user command.
- For both Hysteria 2 and REALITY, actual emulator Wi-Fi/data loss entered
  Waiting, restoring cellular recovered independently captured HTTPS from a
  separate UID, and returning to Wi-Fi recovered automatically. No Reconnect
  command was issued for these network transitions. The same TUN name/index
  remained throughout, including protocol and policy changes.
- Existing separate-UID HTTPS/UDP, App Block, explicit App-over-Site precedence,
  Bypass, counters, normal Disconnect cleanup, encrypted storage, installed-app
  multiselection and Home layout checks all passed. UI cases start with an
  explicit foreground launch independently of the companion/widget tasks.
- Windows x86/x64 application, driver integrity/install, Proxy restoration,
  real TUN capture/policy/UDP/crash cleanup, Home/tray layout and installer
  upgrade checks all passed on isolated Actions runners. Windows runtime
  sources are unchanged; the shared package version is updated.
- Eight release-safeguard unit tests and actionlint passed. Platform handoffs
  use exact run/source cache keys, fail on a missing handoff and verify every
  installer's checksum before publication. Optional emulator report storage
  cannot turn a failed instrumentation test into a passing release gate.

The stronger captured-path check exposed a missing DNS-port exception in the
CI-only Xray fixture. Only its controlled DNS target was allowed; production
VPS policy was not relaxed. Keeping the emulator awake and explicitly launching
each UI case isolates the longer recovery test from foreground-task state.
The full checks above passed together after these harness corrections.

VPS configuration, production DNS/MTU/stack, profiles, app-rule semantics and
P2P code are unchanged. No local binary build or live host network change was
used for this release. Physical Redmi Wi-Fi/cellular, screen-off, captive portal
and OEM process management still require acceptance; see
[recovery acceptance steps](android/RECOVERY.md#physical-device-acceptance).
This does not establish a throughput gain or fix the reported YouTube/ChatGPT
partial-service failures.

# Client 0.7.1 REALITY correction — 2026-11-03

Release source: `31cd4b97919bcc8d64d0a83f5525be2fff9f33e3`.
Runtime changes were complete at `30fba0d`; subsequent commits only adjust CI
emulator provisioning, redundant job inputs and device-operation timeouts.
[Native qualification](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41635231439)
passed. [Unified qualification](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41640656081)
contains the Android APK and Windows x86/x64 installer gates.
All qualification and publication jobs completed successfully. The unified
[Client 0.7.1 release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.7.1)
is stable, non-draft and explicitly Latest. All 12 assets are present, including
the APK, both Windows setup EXEs/portable ZIPs, guides, signature and manifest.
Downloaded release checksums and manifest agree with the qualified APK and
source. `base/client-v0.7.1`, `release/client-v0.7.1` and the release tag all point
to the qualified commit above; `legacy/client-v0.7.0` preserves the prior source.

The phone's original `proxyerror` masked REALITY authentication rejection. An
unmodified official sing-box 1.14.4 client reproduced `reality verification
failed` against the existing VPS on TCP 443 and 8443. An Xray client using the
same local profile succeeded. The pinned sing-box source removed both browser
X25519MLKEM768 extensions; modern Xray requires that hybrid share before optional
X25519. The checked backport retains those shares and supports the corresponding
authentication key. No server credentials, TLS authentication or pin checks were
relaxed. See [upstream investigation](https://github.com/SagerNet/sing-box/issues/4520).

The corrected protocol core was compiled in Actions and run on Windows only as
an isolated loopback SOCKS listener, without changing host routes, adapters, DNS,
system proxy or the user's running VPN. On the current China Wi-Fi route it
returned HTTPS 200 with the VPS exit IP and Google HTTPS 204 on both TCP 443 and
8443, using the existing profile and SNI. This checks the corrected protocol
implementation against the live server; it is not a physical Redmi APK test.
An additional Go 1.27.3 HTTPS client through the corrected core also returned
HTTP 200 and the VPS exit IP on both ports, exercising Go's inner TLS stack as
used by the native health check. No further live network changes were made.

Native CI first asserts rejection with the original upstream implementation,
then verifies repeated authenticated HTTPS against official Xray 26.10.31 after
patching. Wrong short IDs and wrong public keys still fail. Hysteria's repeated
HTTPS and correct/wrong certificate pin checks pass too. The new Xray fixtures
allow only the generated local HTTPS/UDP test targets: freedom otherwise blocks
private addresses by default. This fixture correction does not alter the VPS.

Recovery logs now include the failed stage, a fixed error category and retry
delay. Raw native messages and credentials are not logged in production. New JVM
checks cover category redaction, stale callbacks retaining the backoff, a new
network waking recovery and cancellation. The original session log limits remain.

An earlier full run completed Windows and Android APK qualification but was
canceled because its emulator could not allocate the template's 7+ GiB userdata
partition. It never ran the Android instrumentation tests. The final harness
requests 3 GiB but the SDK image can impose a larger filesystem minimum. It also
removes redundant job-local downloads and unused diagnostic build inputs to
provide headroom, reports startup fatal errors and bounds boot, instrumentation
and cleanup commands so an absent device cannot leave log collection hanging.

Android 14 emulator instrumentation completed `OK (4 tests)` in 93.059 seconds,
including REALITY/Vision through the official Xray gateway, retained TUN across
protocol changes, captured HTTPS from a separate UID, App/Site policy, counters
and disconnect cleanup. All 14 JVM tests and lint passed; versionName 0.7.1 and
versionCode 70100 were verified. The APK signing identity is unchanged.
The downloaded Android package matched its Actions checksum:
`cfb2469986d1b3d533aa0322e8f90ce445dbcb1e4b1058ac6cc2b7ab7e443a88`.

The first Windows x64 TUN check reported an unexpected core exit. The failed job
was rerun with the same source and full checks; no checks were disabled or
relaxed. Windows application/native sources are unchanged from the 0.7.0
baseline apart from the shared assembly/package version. The transient failure's
cause was not established during this Android correction.

# Client 0.7.0 qualification — 2026-11-03

Release classification: normal stable release, explicitly marked Latest.
The initial prerelease classification was corrected without replacing any
qualified binary or changing its signature/checksum. Future client publication
uses stable Latest; development prereleases require explicit separate intent.

[Unified CI run](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41619670984)
and [Client release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.7.0).
Qualified source: `a8065f73b0aba99633ec9c543401c0c3f36905d0`.

All build and qualification jobs passed: bundle safeguards, Android native/app,
Windows x86, Windows x64, Android emulator and Android release packaging. The
first publisher attempt could not create a legacy source ref with its Actions
token. The authorized local GitHub login created the four source refs, then only
the failed publishing job was retried successfully. Installer builds and uploads
were performed in Actions; no local Windows/Android compilation was used.

- Windows x86/x64: application version 0.7.0 verified, policy/integrity checks,
  signed adapter preparation, real Proxy/TUN TCP/UDP and app policy, tray/layout,
  crash cleanup, installer creation and upgrade from 0.6.0 all passed.
- Android: versionName 0.7.0 verified with aapt2; versionCode 70000 derives from
  client/VERSION. JVM tests and lint passed. The release signing certificate is
  unchanged from Android 0.1.0; app ID and encrypted settings format are retained.
- Android 14 emulator: `OK (4 tests)` in 73.441 seconds. The UI selected two
  installed apps, applied Block to both, replaced both with Bypass without
  duplicates, and removed both exceptions back to the default. Real TUN tests
  also covered Hysteria 2/REALITY, different-UID HTTPS/UDP, Block, App-over-Site,
  Bypass, retained TUN, traffic counters and disconnect cleanup.
- App Tunnel screenshot was inspected: names/icons, checkboxes, filters and bulk
  actions are visible in one page with an internally scrollable app list.
- Five release safeguard tests passed, covering complete-platform requirements,
  missing installers, corrupted assets, mixed-version assets and immutable refs.

Release assets include Windows x86/x64 setup EXEs and portable ZIPs, the signed
Android universal APK (arm64-v8a/x86_64), platform guides, signature report,
CLIENT-RELEASE.json and SHA256SUMS.txt. The manifest and source snapshots refer to
the same qualified commit. Older Windows/Android tags and assets are retained.

Source branches: `base/client-v0.7.0`, `release/client-v0.7.0`,
`legacy/windows-v0.6.3` and `legacy/android-v0.1.0`. The first two refer to the
qualified commit; legacy branches refer to their original platform tags.

Physical Redmi interaction, radio handover, screen-off and Xiaomi battery policy
were not tested through ADB. Windows x86 runs under WOW64 in CI. The user's
running Windows VPN was not changed or replaced during this task.
