Android 0.1.0 preview: signed TUN client for Android 10+ (arm64-v8a and x86_64).

REALITY / Vision and Hysteria 2 use imported vpnctl exports or share links.
Profile add/edit/delete, country flags and protocol selector are built in.
App package exceptions win over Site domain/IP/CIDR exceptions; Tunnel, Bypass,
Block and Rules off are supported. Full-leaf certificate pins are preserved.

A foreground VpnService owns capture, socket protection, serialized connection
commands, HTTPS verification and network recovery. Transport retries retain the
TUN descriptor; no automatic Direct fallback. Notification shows live tunnel
rates and totals with Reconnect/Disconnect, plus launcher widget and Quick
Settings tile. Backend logs are bounded RAM-only with Clear/Export.

The APK is built/signed in GitHub Actions. No user credentials are included.
This is a preview: physical Redmi / China-network and Xiaomi power-management
tests remain distinct from CI emulator results. Android allows one VPN per
user/profile, so separate Tailscale/ZeroTier VPN apps cannot run alongside it.
