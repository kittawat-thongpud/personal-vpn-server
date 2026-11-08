# Personal VPN Android client

Install `PersonalVPN-Android-<client-version>.apk` from the unified Client release on the
phone. This is a signed APK installer, not an Android Studio project you must
compile. Android 10+ is required; the APK contains arm64-v8a and x86_64 native
libraries. The first physical target is Redmi Note 13 Pro+ 5G / Android 14.

1. Download the APK through your working connection. Android asks you to allow
   installation by the browser/file manager you use. Review that prompt yourself.
2. Open Personal VPN. Import the device's `xray.json` and `hysteria.json` together,
   or paste the exported VLESS / Hysteria share links. An IP alone is insufficient.
   Prefer a separate device credential: on the VPS run `vpnctl.py add-device android`
   and `vpnctl.py export android` using the existing Python environment; restart
   the server services to apply the added device. Keep exports private.
3. Select profile and protocol on Home. Tap Connect, accept Android VPN consent,
   and allow notifications to see status and live upload/download rates.
4. Test a normal browser/app and verify the VPS exit IP. A Connected label requires
   authenticated outbound HTTPS and a separate HTTPS request through OS capture.

TUN is the capture mode. There is no Windows-style System Proxy selector.
Settings contains VPN configure, App Tunnel, Site Tunnel and backend logs.
Profiles can be added, edited or deleted without reinstalling the APK. Server
credentials stay in app-private AES-GCM storage with an Android Keystore key;
backups are disabled. No profile, VPS password or user logs are bundled.

Protocol names and descriptions share a left edge in the selection sheet.
Settings → Background reliability checks Android power/background/data/notification
restrictions and shows Samsung or Xiaomi manual steps with Settings buttons.
Fresh Launcher entries show only detected restrictions; configured, unknown and
manual-only settings do not trigger recurring pop-ups. Device-specific manual
tips appear once, remembered automatically with no confirmation required; app
updates keep that flag. Recents/notification/widget entries do not request it.
Busy connections keep Cancel accessible before showing advice. Instructions use
short numbered steps. Optional device checks remain available in Settings.
Configured/unavailable readings and last-exit evidence are collapsed.
See
[Background reliability](BACKGROUND-RELIABILITY.md) for coverage and sources.
Settings → Android VPN settings opens the OS Always-on/lockdown controls.

App rules select installed package IDs. Explicit App exceptions precede Site
exceptions. Tunnel, Bypass and Block defaults and Rules off are supported;
Rules off retains the list but ignores that category. Block is rejected inside
the core, not implemented by excluding the app from VPN. Android can assign one
UID to several packages. Every known package participates in App matching;
conflicting exceptions on the same UID use Block, then Tunnel, then Bypass.
These packages share socket ownership and cannot be routed separately by UID.
With App rules enabled, genuinely unresolved ownership is rejected and produces
a bounded `policy.owner_unresolved` notice. A known shared UID produces
`policy.shared_uid`. Services/helper packages may need separate rules. The app's
own control/health traffic is reserved. See [Android routing recovery](ROUTING.md).
Domain matching is limited for independent encrypted DNS or opaque traffic.

App Tunnel displays installed app names and icons. Search, select any number of
apps with checkboxes, choose Tunnel/Bypass/Block, and Apply once. Select existing
exceptions to replace their action, or choose Use default to remove them in bulk.
Selection is retained while searching/filtering; Clear selection resets it.
Exceptions and System apps filters keep the list manageable. Package labels are
cached in memory after background enumeration; only visible rows request icons,
with a bounded cache. The refresh action updates the installed list explicitly.
Applying exceptions while Rules off requires confirming that App rules should
be enabled with Tunnel as the default. Saving rules marks a live VPN connection
for Apply & reconnect, preserving the existing connection until confirmed.

Site Tunnel groups multiple targets under one action. Tap Add site group, choose
Domain or IP, optionally enter a group name, and paste comma-separated or
newline-separated targets. For example `www.google.com,www.youtube.com` or
`2.2.2.*,10.46.136.6,2.2.2.2`. Trailing IPv4 wildcards compile to CIDR:
`2.2.2.*` means `2.2.2.0/24`. IPv6 and CIDR also work. A row shows the domain or
network icon, name (or target summary), action, Edit and Delete. Long summaries
ellipsize; edit to see the full list. Up to 500 effective targets can be saved
across groups. Existing single-target rules are retained and Site rules keep
their insertion order. Explicit App rules still take precedence.

Connecting, Verifying and recovery show an enabled Cancel action, including when
settings have changed. Cancel stops pending DNS/HTTPS and transport work, closes
capture and removes the foreground notification/service. Stopping is shown until
that cleanup finishes. Pending buttons use light text on blue, with explicit
content colors rather than faded disabled text.

The foreground VPN service continues when Home is pressed or another app is
opened. Removing Personal VPN's task from Recent Apps stops the VPN through the
same cleanup path as Cancel/Disconnect. Android's system-managed Always-on VPN
must be turned off in Android VPN settings when that policy prevents stopping.
All entry points share its supervised connection controller. It uses physical-network snapshots,
cancels DNS/HTTPS work from old network generations, protects outbound sockets
from recursion, and keeps the TUN descriptor across transport retries. Network
restoration wakes recovery automatically; unexpected worker completion is retried.
Invalid configuration requires correcting the profile and Reconnect. Authentication
or pin failures remain visible, retry slowly, and wake on a new network; incorrect
credentials or an incorrect clock still need correction.
No automatic Direct or protocol fallback. Existing TCP/QUIC flows can break
during a handover. Normal Disconnect closes capture; Android's VPN
Always-on / Block connections without VPN controls are managed in system settings.
There is no claim that the app can resist Force stop or every OEM power policy.

Connected still requires authenticated outbound HTTPS and an independent request
through OS capture. Primary and secondary health targets are tried before a
verification cycle fails. One failed cycle keeps the core and capture; repeated
failures restart the transport with bounded backoff. Probes are cancellable and
do not block byte-counter reads. Reconnecting/Waiting/Error clear the verified
exit-IP display so an old result cannot imply the new network is connected.

Notification: quiet updates targeting once a second, tunnel upload/download rates,
session totals, Reconnect and Cancel/Disconnect. Android may throttle or hide notifications;
Android 14 permits dismissing many ongoing notifications. Hiding the card does
not disconnect the VPN. Launcher widget: profile/status and connect/disconnect.
Add Personal VPN to Quick Settings for the VPN tile. First-time consent uses
the app Activity; there are no floating desktop-style overlay windows.

Backend logs are RAM-only, bounded to 1,000 records and 1 MiB, with Clear/Export.
Activity recreation retains a running process's session. Process exit starts
fresh. Traffic counts only the selected tunnel payload, excluding Direct traffic;
the graph retains 60 samples. These are not provider billing totals.

Android supports one active VPN per user/profile. Separate ZeroTier, Tailscale
or VPN07 VPN apps cannot be active concurrently in the same profile. Keep the
Windows backup connection available while testing the phone.

All binaries are built in GitHub Actions from the pinned sing-box revision
`c005f35300ce76427dc285481ec5695f5b179cd2`, with a small JNI surface and reviewed
full-certificate SHA-256 pin backport. TLS pin mismatch is tested using real TLS.
Client 0.7.1 also retains the browser's hybrid REALITY key share for modern Xray
servers. Native regression checks reproduce the upstream failure before the
patch and verify authenticated HTTPS after it, including wrong-short-ID
rejection. The Android REALITY emulator gateway is official Xray 26.10.31,
matching the VPS server, rather than another instance of the client library.
Retry logs expose fixed error categories and the failed phase without raw
credential-bearing errors. Stale network callbacks do not skip retry backoff.
The native core is GPL-3.0-or-later; its corresponding source and modifications
are available in this repository and the pinned upstream commit. Preserve its
license when redistributing. APKs use a stable private signing key in Actions
secrets; `SIGNING-CERTIFICATE.txt` identifies the release certificate. Download
SHA256SUMS and SIGNATURE alongside the APK to verify the package.

Emulator results and physical Redmi/China-network results are recorded separately
in VALIDATION.md. Screen-off, Xiaomi battery management and radio handover need
physical-device testing; an emulator pass alone does not establish those results.
