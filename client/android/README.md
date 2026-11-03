# Personal VPN Android — 0.1.0 preview

Install `PersonalVPN-Android-0.1.0.apk` from the private GitHub release on the
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

App rules select installed package IDs. Explicit App exceptions precede Site
exceptions. Tunnel, Bypass and Block defaults and Rules off are supported;
Rules off retains the list but ignores that category. Block is rejected inside
the core, not implemented by excluding the app from VPN. With App rules enabled,
unresolved/shared-UID ownership is conservatively rejected. Services/helper
packages may need separate rules. The app's own control/health traffic is reserved.
Domain matching is limited for independent encrypted DNS or opaque traffic.

The foreground VPN service continues when the app UI is hidden. All entry points
share its connection controller. It observes physical Wi-Fi/cellular changes,
protects outbound sockets from recursion, and keeps the TUN descriptor across
transport retries. No automatic Direct fallback. Existing TCP/QUIC flows can
break during a handover. Normal Disconnect closes capture; Android's VPN
Always-on / Block connections without VPN controls are managed in system settings.
There is no claim that the app can resist Force stop or every OEM power policy.

Notification: quiet updates targeting once a second, tunnel upload/download rates,
session totals, Reconnect and Disconnect. Android may throttle or hide notifications;
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
The native core is GPL-3.0-or-later; its corresponding source and modifications
are available in this repository and the pinned upstream commit. Preserve its
license when redistributing. APKs use a stable private signing key in Actions
secrets; `SIGNING-CERTIFICATE.txt` identifies the release certificate. Download
SHA256SUMS and SIGNATURE alongside the APK to verify the package.

Emulator results and physical Redmi/China-network results are recorded separately
in VALIDATION.md. Screen-off, Xiaomi battery management and radio handover need
physical-device testing; an emulator pass alone does not establish those results.
