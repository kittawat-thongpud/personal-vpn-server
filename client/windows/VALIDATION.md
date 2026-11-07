# Installed Windows preview validation

## 0.9.1 LAN / Wi-Fi and durable recovery — 2026-11-08

- Qualified source `77340fffc7a79bcc2cbb2ec89f97b97f8c99998b`; the
  [complete shared run](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42395956271)
  passed Windows x86/x64, Android and final publication. See
  [LAN recovery](LAN-RECOVERY.md) for the reproduction and implementation.
- Both Windows architectures pass native route selection, six mocked reset
  cases, actual physical flag/journal recovery and preservation of stable native
  properties, DHCP and DNS. Forced GUI death and GUI + guard death recover.
- Real captured DNS/HTTPS, SOCKS UDP over both protocols, external Direct HTTPS,
  both conflicting App-over-Site cases, repeated Proxy/TUN/Disconnect and fresh
  capture-core restart pass. Tests require a new core and successful captured
  HTTPS after killing it, rather than accepting a previous Connected label.
- Installer migration and in-place reinstall pass with settings, stable paths,
  shortcut icons and one persistent adapter/service preserved. x86 is tested
  under WOW64, not physical 32-bit Windows. Physical Explorer tray mouse
  interaction is not established by these CI checks.
- Downloaded x64 Setup SHA-256:
  `9eb086804367f60ff48940cfb7ced262fe3bf481592383c530811e3142521b8a`.
  All published checksum/manifest entries match GitHub digests. No local app or
  native build was used. No 0.9.1 workstation installation or physical LAN
  switch is claimed yet; the active connection was preserved.

## 0.8.0 UI release — 2026-11-06

- Source `0b0f7788951e1325b8b4697531a4fdbe3ce7a651`,
  [final publication](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42184305628),
  [stable Client release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.8.0).
- Both architectures passed the complete existing driver/Proxy/TUN/UDP/policy/
  crash-cleanup and installer migration/reinstall gates. Mobile layouts retain
  a readable Connect button and bounded graph/log viewports at minimum size.
  Shared scale tests cover spikes, gradual settling and session reset while
  same-session core recovery retains history. IPv6 endpoint parsing and TCP
  Unavailable semantics are checked separately from VPN health.
- Home/Settings/Profiles/Site Tunnel/Logs previews were visually reviewed. The
  native tray/taskbar/install path behavior is retained; no physical 0.8.0 host
  replacement was made while the user's working VPN was active.
- An earlier x64 fixture health timeout was followed by successful full runs on
  fresh runners. Cause remains unproven; bounded fixture diagnostics were added.
- Release metadata, all eleven checksum entries and source/platform manifest
  were verified. See the [shared report](../VALIDATION.md) for Android and limits.

## 0.7.3 stable installation and native menus — 2026-11-06

- Qualified source `df9f4d339ddaa41a50fcfd5c1b77872a5f763467`;
  [unified Actions run](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42154976011)
  completed successfully. The
  [Client 0.7.3 release](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.7.3)
  was stable, non-draft and Latest at publication, including Windows x86/x64 and Android.
- Both Windows matrices passed application/architecture checks, signed driver
  installation, Proxy restoration, real TUN capture, executable policy, UDP,
  crash cleanup, layout and native HMENU definition/command checks.
- Actual Setup migration from published 0.7.2 and same-version reinstall passed
  while idle GUIs were running. Both ended at `C:\Program Files\PersonalVPN`
  with a single uninstall identity, a running service at the stable executable,
  byte-identical encrypted settings and valid large/small Windows icon extraction.
  Owned old files were retired and an unowned legacy file survived.
- Both old user shortcut names were tested. The canonical `Personal VPN.lnk`
  points to the stable executable/standalone ICO; recognized `PersonalVPN.lnk`
  links were consolidated. The x64 setup log confirms the Program Files alias
  correction. A read-only check on the development host independently showed
  its 0.7.1 link resolving to Program Files in a 64-bit Shell COM client and
  Program Files (x86) in a 32-bit client. No shortcut was changed by that check.
- All four final installer bootstraps succeeded on attempt 1. A previous x86
  reinstall hit device-readiness failure. Setup now records native device/SCM
  errors and can retry once only if the service stopped after its deadline.
  The bounded recovery branch was not fault-injected in this qualification;
  the previous failure's exact cause was not established by its generic log.
- Taskbar lifecycle checks passed. Actual mouse input was unavailable on both
  Actions desktops and explicitly reported as **not passed**. Windows 11 tray
  overflow interaction still requires physical desktop acceptance; native menu
  definition checks do not establish that Explorer behavior.
- The downloaded x64 Setup SHA-256 matched the release checksum and GitHub asset
  digest: `22c901d79f2d4f0f5727747c8fb007566465eb53badb6c45b422de04b523c4a6`.
  All 11 checksum entries matched GitHub asset digests; the eight platform
  files also matched the source manifest.
  Large non-selected packages were not downloaded again locally.
- The active development-host VPN has not been replaced yet. Installation and
  actual tray checks await a working backup connection and normal exit of the
  old active TUN client. See [Shell acceptance](SHELL-INSTALL.md).

## 0.6.3 tray opening and dismissal — 2026-11-03

- Runtime source `320c27a`; x86/x64 builds, native TUN regression checks and
  Setup upgrade checks passed in
  https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41584317743.
  Release: https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.6.3.
- Fixes two concrete menu bugs: the first empty popup inherited Cancel=true
  before Opening populated it; canceling every AppFocusChange also prevented
  dismissal after switching to another application. Opening now sets the final
  cancel state after rebuilding, and standard shell dismissal is retained.
- The regression test calls Show on the initially empty native ContextMenuStrip
  and asserts visibility after one request. Each AppFocusChange, AppClicked,
  Keyboard and ItemClicked Close request hides the popup. This tests real popup
  events and visibility, not physical tray mouse injection.
- Installed x86 Setup SHA-256:
  `3cc1a7c5833a0d687ccb05fa1ce917e982857d2beb69ca1dc24fe4956edefe49`.
  Host Setup exit 0 and adapter preparation PASS on the first attempt; protected
  settings hash unchanged. The installed layout / tray tests passed as well.
- The updated GUI was left connected. Ordinary no-proxy curl verified exit
  `45.77.179.227` and Google HTTP 204. ZeroTier self and peer ping passed.
  Exactly one PersonalVPN adapter remained, with the same GUID recorded for
  0.6.2. VPN07 was preserved. No TUN runtime changes were made in this release.
- Host evidence: ignored `.cache/windows-client-0.6.3/validation/activated.json`
  and `layout.json`. The desktop automation inventory did not expose the
  tray-only Personal VPN window, so actual desktop right-click / outside-click
  behavior has not been independently observed by UI automation.

## 0.6.2 captured traffic on the development host — 2026-11-03

- Release / source: https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.6.2
  (`1fe9bc9`). Both architecture builds, installer upgrades and native TUN
  integration checks passed in Actions run
  https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41581428268.
  Build and packaging ran in Actions; no local binary build/upload was used.
- Installed x86 Setup SHA-256:
  `e2685eb59512ecff4875b7a61a38cb1f74b9db391be5f400f2d42d269638ae41`.
  The first installed adapter-service start failed readiness. An explicit retry
  of the installed `--install-adapter` succeeded. This is a recovered bootstrap,
  not a clean first-attempt Setup pass; its precise cause was not established.
- Actual operator-run TUN tests passed against VPS `45.77.179.227` with both
  Hysteria 2 and REALITY. HTTPS egress matched that IP, an ordinary external
  curl reached Google with HTTP 204, and Google DNS returned IPv4 addresses.
  REALITY uses the current server SNI `www.amazon.jp`; credentials and TLS
  certificate checks were retained.
- TUN / Proxy / TUN / Disconnect cycles restored the previous Windows proxy,
  retained the device, and kept adapter GUID
  `5e9eb91b-8623-d4f2-4fd4-128fc6c10fa5` throughout. One persistent adapter service
  remains running. The GUI was left connected in TUN / Hysteria 2 mode.
- From the connected GUI session, Windows manual proxy was disabled, no-proxy
  curl reported `45.77.179.227`, and Google returned HTTP 204 without forcing
  curl's IPv4 option. A subsequent check repeated HTTP 204 successfully.
- ZeroTier self `10.46.136.123` passed throughout. Peer `10.46.136.125` timed out
  once during Hysteria testing, passed under REALITY, and passed again after
  the final Hysteria GUI connection and a later read-only check. No ZeroTier
  service repair, rejoin or restart was performed; VPN07 was preserved.
- The demonstrated 0.6.1 failure was address-family dependent: default / IPv6
  curl to Google reset while IPv4-only curl returned HTTP 204. Version 0.6.2
  defaults TUN DNS to `ipv4_only` without disabling Windows or mesh IPv6.
  IPv6 DNS is an explicit option for VPS routes that support it. Literal IPv6
  destinations still require working upstream IPv6.
- Local evidence is ignored and contains no committed profiles:
  `.cache/windows-client-0.6.2/validation/activated.json` and `tun-host.json`.
  The earlier `installation.json` deliberately retains the initial Setup failure.
- x86 runs were under WOW64, not physical 32-bit Windows. There is no
  whole-machine kill switch; existing flows can be interrupted during recovery.

## 0.6.1 upgrade and host validation — 2026-11-03

- Actions run `41571324076` passed both architecture matrices and Setup
  upgrade checks. Inno Setup no longer reuses a previous version's directory.
- The installed host package passed driver preparation, native REALITY and
  Hysteria HTTPS probes, self-tests and layouts; installation preserved the
  encrypted settings byte-for-byte. Idle capture cleanup and atomic crash-test
  readiness fixes were included.
- Real TUN mode transitions and captured HTTPS passed on the host, but ordinary
  curl exposed the IPv6-destination failure described above. Therefore 0.6.1
  is superseded by 0.6.2 for this host's Internet compatibility.

## 0.6.0 persistent adapter lifecycle — 2026-11-03

- Release / source: https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.6.0
  (workflow target 102f47b, runtime service handoff fix d88ff90).
- Both x86 and x64 builds, native integration tests and actual Setup installation
  passed: https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41499328725.
- The automatic Windows service holds one Wintun device. The pinned source-built
  sing-box core only opens that existing device; ordinary sessions never create
  or delete it. Native hashes are embedded in the managed application.
- Actual CI tests cover authenticated DNS/HTTPS capture, IPv4 / IPv6 route
  readiness, SOCKS UDP relay, external executable Block, App Tunnel over Site
  Block, repeated Proxy/TUN/Disconnect transitions, forced core death, a stale
  owned loopback proxy, and abrupt parent death. One adapter GUID/count and the
  same service process are asserted across connection cycles. Stop resets only
  owned capture routes, addresses and DNS; physical DNS is unchanged.
- TUN suspends WinINet manual/PAC selection through a guarded ownership lease.
  A canceled/faulted worker no longer prevents later Connect or mode changes.
  Network changes on the owned adapter no longer trigger upstream reconnects.
- Installer handoff tolerates already-stopping service states (Windows 1061),
  waits for the prior process to exit, and propagates preparation failure as a
  nonzero Setup exit. Both architecture installers passed this upgrade path.
- Downloaded x86 installer SHA-256:
  d754fed8e9222f038a13f8816719d071ae900c2a48ac1b1cfe8b6b6700b7b580.
- The development-host installation attempt was canceled at Windows UAC before
  Setup started. No 0.6.0 host TUN activation or installed-VPS probe is claimed.
  The current connected version was preserved. An encrypted pre-upgrade settings
  backup was saved under the ignored .state client-test directory.
- Before the update, read-only host checks again confirmed the on-link ZeroTier
  10.46.136.0/24 route and successful self / peer ping to 10.46.136.123 and
  10.46.136.125 (peer 208 ms). No ZeroTier repair or service restart was performed.
- x86 is tested under WOW64, not on physical 32-bit Windows. There is no
  whole-machine kill switch; existing flows can be interrupted during recovery.

## 0.5.0 Administrator TUN and Windows installers — 2026-11-03

- Runtime source: `2c6677a`; all x86 / x64 build, native integration and
  installer checks passed:
  <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41484530050>.
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.5.0>.
  Includes x86 (32-bit) and x64 Setup EXEs, self-contained ZIPs, UI previews
  and SHA256SUMS. No local compilation or binary upload was used.
- The manifest requires Administrator. Setup contains pinned native cores
  and vendor-signed Wintun 0.14.1. x86 WinINet uses its correct native union
  alignment; the x86 package includes 386 cores and is tested under WOW64.
- Actual CI TUN tests (Hysteria 2) passed on both architecture builds:
  adapter creation and captured HTTPS, native UDP forwarding, external-process
  Block, explicit App Tunnel over a conflicting Site Block, owned-device
  removal / physical DNS retention, and abrupt-parent cleanup.
- Both Setup EXEs compiled and installed silently on isolated Windows runners.
  x86 cleanup on 64-bit Windows uses the native device manager for the exact
  recorded device. Device cleanup has a deadline; errors are surfaced.
- Home protocol selection replaces the old badge. Logs have no explanatory
  banner or size counters; internal 1,000-record / 1-MiB bounds remain.
  The tray close decision retains focus-change menus while allowing outside
  click, Escape and selected actions to close them. Synthetic renders and
  bounded log widgets passed at small, default and wide sizes.
- Downloaded x86 installer SHA-256:
  `2f3133afe43f96c3dbb7a8b8e123341de8edbeace0c07ef5f974a0aaf9ef2a86`.
- Installed x86 on the development machine in
  `C:\Program Files (x86)\PersonalVPN\0.5.0-x86`.
  Driver preparation, installed-package self-tests and layouts passed.
  Legacy per-user shortcuts were updated to the new executable.
- REALITY and Hysteria 2 isolated native probes both verified HTTPS exit
  `45.77.179.227` from the installed package. Protected settings were
  byte-for-byte unchanged. An encrypted pre-upgrade DPAPI backup was kept.
- The old connected client remained running. No TUN connection or route
  takeover was activated on the development machine. User-side captured TUN
  traffic from China, REALITY-over-TUN and physical 32-bit Windows are not
  claimed as live-tested here.
- ZeroTier initially showed a disconnected adapter and tentative
  `10.46.136.123/24`, with no mesh route. Later read-only Administrator
  diagnostics showed network OK, adapter Up, preferred address and the
  `10.46.136.0/24` route. Both self and peer `10.46.136.125` ping passed
  before and after installation. No repair / join / service restart was
  executed; the cause of the recovery was not established.
- Known boundary: no whole-machine kill switch; reconnect may release capture.
  Existing mesh inner prefixes are excluded, but live user-side TUN / mesh
  coexistence still needs an operator-run test.

## 0.4.1 tray client and capture selector — 2026-11-02

- Runtime source: `e1fc9db`. Build and all CI checks passed:
  <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41470005168>.
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.4.1>.
- Downloaded ZIP SHA-256: `c388ac117f1db8c6076cecad2564d66c0080791b23823a5561eb899f794c27e1`.
- Source and icon assets were pushed; compilation, native-core packaging and
  release publication ran only in GitHub Actions.
- Downloaded-package self-tests and synthetic UI / tray checks passed on the
  development machine. All pages rendered at 380x590, 438x750 and 1280x760.
  Home has no page scroll, and the Connect button is checked for a minimum
  visible height of 96 DIP. Compact / default Home, Settings and the TUN
  confirmation renders were visually inspected.
- Tray checks cover menu dispatch, selected profile / protocol, available
  actions, busy guards, idempotent disposal, closing the window without
  clearing session logs, and remaining outside the taskbar. The synthetic
  test does not register a visible shell tray icon. Manual interaction with
  the real tray shell is left for the user after reopening the installed app.
- App Tunnel is visible but disabled in Proxy. The capture selector is locked
  during backend operations. Unsupported TUN cannot be confirmed or started
  through the backend. Selecting it preserves Proxy and existing app drafts.
- CI also passed native proxy ownership / conflict / restoration / crash
  recovery tests with an ephemeral local Hysteria server, active Site policies,
  and the Wintun plan schema check using the pinned upstream validator.
- Genuine-device protocol probes from this package verified HTTPS exit
  `45.77.179.227` with REALITY and Hysteria 2 (about 1.6 s each). These probes
  used isolated loopback listeners and did not change Windows proxy / routes /
  DNS / firewall or saved device credentials.
- Installed per-user with `-NoLaunch`. Desktop / Start shortcuts now target
  0.4.1 and its embedded icon; the installed executable matches the package.
  Protected settings were byte-for-byte unchanged across validation and
  installation. An encrypted DPAPI backup was kept before the upgrade.
- The existing connected 0.3.2 GUI and owned cores were left running. The
  installed 0.4.1 GUI was not launched over that live session. No Wintun
  adapter or elevated helper was installed or activated.

## Historical 0.2.3 browser capture update — 2026-11-02

The initial IP mismatch was a capture gap: Windows still used the other VPN's
proxy at `127.0.0.1:7688`. Version 0.2.0 verified tunnel health but did not apply
Windows browser proxy settings. Version 0.2.3 implements verified-then-apply
System proxy capture, conditional restore, an independent restoration watchdog,
external proxy conflict reporting, and a deferred final window close.

- Source: `6165d93`.
- Build / self-tests: <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41432492002>
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.2.3>
- Downloaded ZIP SHA-256: `8554841a892537a6116c85ff36ce79c7176d62389865cb019b611735105b8650`.
- Per-user installation succeeded without administrator rights. The installer
  updates the DACL access section directly, avoiding an upgrade failure caused
  by PowerShell Set-Acl requesting a security privilege.
- Installed self-tests passed, including native WinINet proxy query / ownership
  checks. No credentials or backend session logs are stored in the release.
- The GUI was opened disconnected with Hysteria 2, Global and System proxy
  selected. Existing device credentials and rule entries were retained.

Repeated native tests using the genuine device export:

| Test | Observed result |
| --- | --- |
| Hysteria 2 on OS route, without the other HTTP proxy | VPS exit `45.77.179.227`, four parallel HTTPS requests passed |
| Google generate_204 over Hysteria SOCKS | HTTP 204 |
| Wikipedia over Hysteria SOCKS | HTTP 200 |
| REALITY through the other HTTP proxy | VPS exit and four parallel requests passed |
| REALITY on OS route | Connection reset |
| Unauthenticated TCP 443 | TCP connection succeeded |
| TLS fallback with Amazon SNI on TCP 443 | Connection reset |
| Experimental client fragmentation setting | Connection reset |

Only Wi-Fi was reported as an Up network adapter during these probes. The native
Hysteria configuration did not use the other VPN's HTTP proxy. This establishes
working HTTPS through the selected VPS on the tested route; it does not promise
future availability or establish which network component resets TCP / TLS.

**System proxy acceptance remains pending.** An earlier 0.2.1 integration attempt
failed with HttpRequestException during external HTTPS testing and restored the
previous Windows proxy. It is not counted as an end-to-end pass. The newer test
distinguishes a website's HTTP error response from a failed tunnel. Automatic
approval review rejected the combined download / install / proxy-test command
with `blocked by policy`, so the final 0.2.3 System proxy, crash-watchdog and
normal-browser checks were not rerun automatically. Download, checksum,
installation and non-mutating self-tests were completed separately. The user
was asked to click Connect and verify the ordinary browser's numeric exit IP and
Google access. Do not describe that acceptance as passed until confirmed.

WinINet integration follows the per-connection query/set API and restore flags
described in [Microsoft's documentation](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_optionw).

## Historical 0.2.0 validation

Validated on Windows 11 x64, 2026-11-02, using the package downloaded from the
private GitHub Release `windows-v0.2.0`. The development machine did not compile
or upload native application binaries. Build source: `5e4b876111217672e783f6ec909e8757c095c498`.

- GitHub Actions run: <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41427624248>
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.2.0>
- ZIP SHA-256: `d7faf3ebee8df58cc8975b43e4e428d67a768a9c99fcff176b3f6afe2d6fe607`
- Per-user installation: `%LOCALAPPDATA%\Programs\PersonalVPN\0.2.0`.
- Desktop and Start menu shortcuts created. Imported genuine device exports into
  Windows DPAPI protected settings; no credentials are bundled in the release.

**Installed binary checks: passed**

- Backend-only ring: 5,000 input records bounded to 1,000 and at most 1 MiB;
  message truncation, UI-event exclusion and Clear to zero.
- Health inbound always tunnels independently of user rules. Block precedence,
  Global / Rules defaults, Disabled retention and invalid domain rejection.
- Import discards supplied listener / log configuration and retains full
  certificate SHA-256 pin enforcement. Bundled core hashes match pinned values.

**Real VPS integration through the installed supervisor: passed**

- Hysteria 2: verified HTTPS exit `45.77.179.227`; four parallel HTTPS requests
  through the app's HTTP listener returned the same VPS exit.
- Deliberately killed the owned Xray router: visible retry / new authenticated
  connection; HTTPS through the proxy worked again.
- Explicit literal-IP Bypass reached an owned loopback HTTP fixture. Changing
  that rule to Block prevented the request while independent tunnel health
  remained verified.
- Disconnect closed the owned proxy listener; no probe core remained running.
- Wrong Hysteria certificate pin never reached Connected.
- REALITY through the existing HTTP proxy `127.0.0.1:7688` reached the VPS exit.
- Clearing the live backend ring left zero records. Test reports are explicit
  diagnostic artifacts; the application does not automatically persist logs.

**Native UI check**

Launched the installed GUI with the imported profile and clicked Connect. Home
showed Connected, Hysteria 2 and the verified exit `45.77.179.227`. An independent
HTTPS request through its SOCKS listener `127.0.0.1:17891` confirmed that exit.
The centered Connect / Disconnect label and the Logs page were inspected in the
native window. Logs remain inside the page's available height with internal
scrolling. UI navigation did not produce backend log records. The Test browser
action created a separate Edge window with the exit-check URL; the browser page
itself was not visually verified because that window was minimized.

System proxy configuration, default routes and DNS server settings compared
equal before / after these tests. No other VPN was stopped. The regular GUI was
left connected in Hysteria Proxy + Global mode for the user's testing. Global
applies only to captured proxy traffic.

**Limits**

The current OS route may itself pass through the user's other VPN. These results
do not prove independent traversal of the Great Firewall, continuous availability,
or access to a particular third-party service. Direct REALITY previously failed
on this host; the chained result must not be described as a direct path.

No physical Wi-Fi / cellular switch, sleep / resume, TUN, DNS-leak qualification,
whole-machine kill switch, process interception or mesh interoperability test was
performed on the live development host. These remain separate acceptance work.

## Shared client 0.7.0

Both Windows architectures were rebuilt and qualified with the shared client
version. See [shared qualification](../VALIDATION.md). Historical host checks
below remain evidence for their recorded versions; the running user installation
was not upgraded automatically as part of the Android App Tunnel change.

