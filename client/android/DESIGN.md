# Android client design — 2026-11-03

Status: implemented Android 0.1.0 preview. Current qualification evidence and
physical-device limitations are recorded in VALIDATION.md. The decisions below
retain the original platform analysis; they are not physical-phone test results.
Primary acceptance device supplied by the user: Redmi Note 13 Pro+ 5G,
Android 14, build UP1A.231005.007. The Xiaomi ROM version, ABI list and battery
policy have not been read from the phone.

## Recommended first version

Use native Kotlin / Jetpack Compose for the application and Android VpnService
for capture. Start with TUN, REALITY and Hysteria 2; keep local HTTP/SOCKS proxy
as a later optional tool. Build a signed arm64-v8a APK in GitHub Actions after
confirming the phone ABI; use x86_64 for emulator integration. Propose Android
10 / API 29 as the minimum for the full per-flow application policy: the active
VPN can query TCP/UDP connection ownership through getConnectionOwnerUid from
API 29. This baseline is a product decision, not an Android VPN requirement.
[Connection ownership API](https://developer.android.com/reference/android/net/ConnectivityManager#getConnectionOwnerUid(int,%20java.net.InetSocketAddress,%20java.net.InetSocketAddress)).

The implementation uses a pinned libbox Android library. Physical acceptance must prove existing
VLESS / REALITY / Vision and Hysteria 2 profiles against the VPS, including the
current REALITY SNI and Hysteria certificate pin. It offers Android package-name
rules, unlike Windows process paths. If compatibility fails, assess separate
native libraries behind the same platform interface. Windows EXEs and the
OpenAdapter-only Wintun patch are not Android runtime components.
[Core routing reference](https://sing-box.sagernet.org/configuration/route/rule/).

## Platform differences

| Windows feature | Android behavior / proposed decision |
| --- | --- |
| Wintun installation / Administrator | No Wintun driver or root. Ask for Android's VPN consent. |
| Persistent installed adapter | Android owns the TUN file descriptor. Establish on Connect, close on Disconnect; retain it across upstream retries where possible. Do not port Windows service/device persistence literally. |
| System Proxy mode | No equivalent reliable capture of every app. A VPN HTTP proxy is advisory, may be ignored, and has split-routing limitations. TUN is the primary mode. |
| Select `.exe` | Select installed package names, display app names/icons, resolve connection UID. Shared-UID apps cannot be promised independent rules. |
| Tunnel / Bypass / Block app rules | Proposed via captured-flow ownership and core routing. Android's OS allow/deny lists alone cannot express all three. |
| Site domain / IP / CIDR rules | Core rules can be retained; traffic attribution to a domain is limited when apps hide DNS or use opaque connections. IP/CIDR remains useful. |
| ZeroTier / Tailscale coexistence | Their separate Android VPN apps cannot occupy the same user's VPN slot simultaneously. A unified engine or external mesh gateway is separate future work. |
| Tray / desktop popups | Navigation, sheets and dialogs inside the app; Quick Settings tile, notification and launcher widget outside it. No floating overlay permission. |
| UI closed / screen off | Connection belongs to the VPN service, not the Activity. Reopening the UI attaches to its current state. |
| App rules in local proxy | Disabled: a proxy connection does not provide reliable caller-package attribution. |

The OS provides VPN consent, socket protection and a TUN descriptor; only one
VPN is active per user/profile. An already running Android VPN must be replaced,
not silently chained. User/profile scope also limits app selection.
[Android VPN guide](https://developer.android.com/develop/connectivity/vpn),
[VpnService API](https://developer.android.com/reference/android/net/VpnService).

VpnService.Builder's allowed and disallowed lists are mutually exclusive.
Excluded apps use the underlying network; exclusion does not block the app.
Changing those OS lists requires re-establishing capture. For the full rule
mode, capture eligible applications and route inside the core rather than
excluding Bypass apps at the OS boundary.
[Builder contract](https://developer.android.com/reference/android/net/VpnService.Builder).

## Policy contract

Preserve the existing explicit-App-over-Site precedence:

1. Reserved VPN transport protection and internal DNS / health paths.
2. Explicit package exception: Tunnel, Bypass or Block.
3. Explicit domain / IP / CIDR exception.
4. Enabled App category default; otherwise enabled Site default; otherwise Tunnel.

The four existing category modes remain: Tunnel by default, Bypass by default,
Block by default, or Rules off. Rules off retains the saved exceptions but
ignores that category; it does not disconnect the VPN. With both categories off,
eligible captured traffic uses the VPN.

Block stays in capture and is rejected by the core. Bypass uses a deliberately
protected direct socket. When application rules are active, unresolved UID,
unsupported ownership lookup or ambiguous shared UID must not silently grant
Direct access; use a conservative blocked result and a bounded diagnostic until
the case is supported. Existing connections may need reopening after a rule
change. Test isolated processes, UDP and shared UID behavior before claiming
complete application control.

App picker: normal apps first, separate system-app section where visible,
search by name/package, stable package IDs in stored rules. Android package
visibility can filter results, so a complete picker needs explicit visibility
design and device testing. Windows executable rules are marked incompatible
on import rather than converted to Android package rules.
[Package visibility](https://developer.android.com/training/package-visibility).

Do not promise perfect domain matching for independent DoH, encrypted metadata,
or direct-IP apps; do not introduce TLS interception. Test captured DNS, cached
answers, IPv4/IPv6 and QUIC. Retain explicit IPv6 handling so unsupported IPv6
does not silently escape capture.

## Service and recovery

One connection controller serializes commands from UI, notification, tile and
widget. Use session generations to discard late callbacks. Connection intent
is separate from current health. Proposed states: Disconnected, Preparing,
Connecting, Verifying, Connected, Waiting for network, Reconnecting, Error.

Protect gateway and intentional Direct sockets from VPN recursion and bind
them to the selected underlying network. Observe underlying Wi-Fi/cellular
capabilities and link changes, not changes produced by our own VPN. Update the
VPN's underlying-network report and restart only the failed transport when
necessary. Keep the TUN descriptor during upstream recovery if its capture
configuration is unchanged. TCP sessions can break during handover; do not
promise seamless continuation for either protocol.
[VpnService socket/network APIs](https://developer.android.com/reference/android/net/VpnService),
[Network callbacks](https://developer.android.com/develop/connectivity/network-ops/reading-network-state).

Verify authenticated HTTPS through the selected outbound before reporting
Connected. Retry with bounded backoff/jitter and reset retries after a validated
network change; no automatic Direct fallback. While capture is retained during
recovery, reject or boundedly discard traffic instead of queuing without limit.
Handle consent cancellation, null establish(), onRevoke(), service destruction
and repeated Disconnect idempotently. Revocation by another VPN ends our session;
do not fight the user's new VPN selection.

Foreground-service behavior must follow Android 14's declared type and
eligibility requirements. Configured VPN apps are an eligible systemExempted
case; establish consent/configuration before relying on that exemption. Post
the foreground notification promptly, including starts from a tile/widget.
[Foreground service types](https://developer.android.com/develop/background-work/services/fgs/service-types).

System Always-on VPN / Block connections without VPN is user-controlled.
Excluded apps can lose access under lockdown. For a strict product lockdown
option, reject conflicting Bypass policies; OS lockdown does not validate the
core's own protected Direct decisions. Widget/tile Disconnect must reflect
Always-on control and guide the user to VPN settings when appropriate.
[Always-on and lockdown](https://developer.android.com/develop/connectivity/vpn#always-on).

On Redmi, test screen-off, battery saver and process recreation before offering
autostart/battery-setting guidance. Xiaomi documents user-controlled background
autostart, but the exact menu/version on this phone is not verified. Do not
claim an exemption defeats Force stop or every OEM process policy.
[Xiaomi autostart guidance](https://dev.mi.com/xiaomihyperos/documentation/detail?pId=1624).

## App, notification, widget and tile

- App Home: profile, protocol selector, centered Connect/Disconnect, current
  health, exit IP and traffic graph. Keep Home compact; lists/logs scroll
  internally. Settings contains protocol config, App Tunnel, Site Tunnel and
  backend diagnostics. Dialogs/sheets remain inside app navigation.
- Notification while the service runs: profile/protocol/status, up/down bytes
  per second, connection totals, Reconnect and Disconnect. Tapping the body
  opens Home. Use one notification ID and quiet updates, targeting at most one
  update per second. OS delivery is best effort; never queue missed samples.
- Sample monotonic core tunnel-byte counters with elapsed real time; distinguish
  VPN payload from intentional Direct traffic and device-wide traffic. Keep
  the in-app graph at 60 samples. When the display is off, retain counters but
  reduce rendering work; resume current rates rather than replaying old updates.
- Launcher widget: compact connect/disconnect, profile and real service status.
  Refresh after commands and state transitions; it is not a per-second chart.
  A fresh connection needing consent opens the app's consent flow. Disconnected
  widgets never poll or hold a permanent worker just to show status.
- Quick Settings tile: connect/disconnect with busy/error state. It uses the
  same command controller and does not start a second VPN instance.

Android can throttle notification updates; Android 13+ requires notification
permission for drawer visibility, and Android 14 generally permits dismissing
ongoing foreground notifications on unlocked phones. Do not equate a dismissed
notification with a disconnected VPN or promise a permanently visible card.
Show denied-permission guidance inside the app. Arbitrary speed text in the
system status bar is not a portable UI guarantee.
[Notification updates](https://developer.android.com/develop/ui/compose/notifications/create-notification#Updating),
[Notification permission](https://developer.android.com/develop/ui/compose/notifications/notification-permission),
[Android 14 dismissal changes](https://developer.android.com/about/versions/14/behavior-changes-all#non-dismissable-notifications).

Widget scheduler updates have a 30-minute minimum; state-driven updates from
the active service are separate. This is why the notification, rather than
scheduled widget refreshes, carries live rates.
[Widget updates](https://developer.android.com/develop/ui/views/appwidgets/advanced),
[Quick Settings tiles](https://developer.android.com/develop/ui/views/quicksettings-tiles).

Backend events only: connection, retry, handover, exception and result. Keep
1,000 records / 1 MiB in RAM, clear/export on demand, no credentials or full
browsing history. Activity recreation does not reset a live backend's logs;
process exit starts fresh. Keep profile secrets in app-private encrypted
storage with a Keystore-protected key; never package a user's profile in the APK.

## Build and acceptance order

1. Library compatibility spike on the Redmi: import a separate Android device
   profile and authenticate both protocols against the existing VPS.
2. VpnService capture, protected sockets, authenticated HTTPS/DNS, IPv4/IPv6
   behavior and exact cleanup; compare exit IP with the VPS.
3. Package policy proof: App exception over conflicting Site rule, all four
   defaults, actual Block vs Direct, UDP, unresolved UID and shared UID cases.
4. Repeated Wi-Fi/cellular transitions, airplane mode, gateway loss, screen-off,
   battery saver, process/service death and user revocation. Confirm no Direct
   fallback while our capture remains active; distinguish OS lockdown behavior.
5. Native UI, notification rates/actions, launcher widget, Quick Settings tile,
   notification denial/dismissal and phone restart / Always-on behavior.
6. Actions builds/tests and signed APK release, immutable source/dependency pins,
   signing secrets in CI, SHA-256 checksums and stable signing identity for
   updates. No local heavy binary upload. Review dependency licensing when
   choosing the actual native library distribution.

An emulator validates OS integration; real China-network throughput, Redmi
power management and radio handover need the supplied physical phone. No APK,
working Android tunnel or phone-side measurements are claimed by this document.
