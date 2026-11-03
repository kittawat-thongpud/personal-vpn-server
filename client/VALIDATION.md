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
