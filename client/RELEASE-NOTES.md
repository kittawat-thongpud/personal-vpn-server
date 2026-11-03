Client 0.7.0 preview is the first shared Windows and Android feature release.

Android App Tunnel now shows installed app names and icons in a searchable list.
Select multiple apps with checkboxes, choose Tunnel / Bypass / Block and save all
exceptions together. Existing exceptions can be replaced or reset to the default
in one action. System apps and exceptions-only filters are available. Package
enumeration runs in the background and is cached; icons load for visible rows
with bounded caching. App exceptions still precede conflicting Site rules.

Windows x86/x64 installers and portable ZIPs plus the signed Android APK are
built and qualified in GitHub Actions from the same source/version. The release
contains one checksum list and a source/asset manifest. Previous releases are
preserved. Source snapshots: base/client-v0.7.0 and release/client-v0.7.0; older
platform baselines are retained under legacy/. Future unified releases archive
earlier client source under legacy/client-v*.

Windows retains Proxy/TUN, persistent Wintun and mesh route support. Android uses
VpnService TUN and package rules, with notification speeds, widget and tile.
A shared release version identifies the feature baseline, not identical OS APIs.
Android supports one VPN per user/profile; separate ZeroTier/Tailscale VPN apps
cannot run concurrently in that profile. Windows x86 is tested under WOW64.
Physical Redmi radio handover, screen-off and OEM battery policies require device
acceptance beyond the Android 14 emulator checks. No user credentials are bundled.
