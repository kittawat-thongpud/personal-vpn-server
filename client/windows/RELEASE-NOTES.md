Client 0.9.4: shared Client release for focused Android background guidance and numbered steps.
Windows retains its existing VPN runtime and includes the fixes below.

Client 0.9.3: shared Client release for Android Cancel, contrast and task-removal cleanup.
Windows retains its existing VPN runtime and includes the fixes below.

Client 0.9.2: shared Client release for the Android shared-UID App routing fix.
Windows retains its 0.9.1 VPN runtime and LAN / Wi-Fi recovery behavior below.

Client 0.9.1: LAN / Wi-Fi destination egress and crash-safe physical binding recovery.

See ../RELEASE-NOTES.md for the current unified client release. Destination-aware
Direct sockets preserve App precedence without global Site host routes. Physical
binding changes have a durable GUID/family journal owned by the independent guard;
native code no longer changes these flags implicitly. Cleanup verifies capture
routes/DNS and proxy restoration and retains recovery data on failure.

Historical notes follow (installation now uses the stable main folder):

Windows 0.6.3: tray menu opens on the first request and dismisses normally.

Fixes an initially empty WinForms menu inheriting Cancel=true before its Opening handler populates the actions, which required a second right-click. Also removes the blanket AppFocusChange cancellation so clicking another application dismisses the popup. Pointer movement alone uses the standard shell menu behavior. Regression checks open the initially empty native popup and verify focus-change, outside-click, Escape and action close reasons.

Includes the 0.6.2 IPv4-compatible TUN DNS and persistent adapter fixes:

The default TUN DNS strategy now returns IPv4 addresses. This fixes the observed case where the VPS IPv4 path worked, while applications selecting an IPv6 address failed during TLS. VPN configure includes an explicit IPv6 DNS option for servers with working IPv6 Internet access. Windows IPv6 and mesh interfaces are unchanged; IPv6 address space remains captured by TUN.

CI and host diagnostics now test ordinary external curl against Google without forcing an address family, and verify that the default DNS does not advertise unusable AAAA destinations.

Setup explicitly uses the new version directory instead of the previous installation path. CI installs the previous version first, verifies the new default destination and checks that the previous executable stays unchanged. Native binaries are reused from the SHA-256 pinned, Actions-tested 0.6.0 bundle when the source recipe is unchanged.

Setup installs the signed Wintun driver and a small automatic Windows service. The service holds one PersonalVPN adapter. Connect, Disconnect, reconnect and Proxy / TUN switches reuse that device; they reset session routes, addresses and DNS rather than deleting and recreating adapters. The TUN core is built in GitHub Actions from a pinned source revision with an OpenAdapter-only patch.

TUN temporarily suspends Windows manual / PAC proxy capture, preventing traffic from being sent to a stale local proxy. Disconnect conditionally restores previous settings, with stale owned loopback settings normalized to Direct. Captured HTTPS must match the authenticated server exit before Connected. Faulted workers no longer prevent a subsequent reconnect or mode change.

x86 (32-bit) and x64 installers and portable ZIPs require Administrator. The protocol selector remains on Home, App exceptions precede Site exceptions, backend logs retain their session bounds without UI counters, and tray behavior is preserved. Existing mesh inner prefixes remain excluded.

CI tests real capture, UDP, app rules, repeated Proxy / TUN / Disconnect cycles, core crash recovery, stale proxy recovery and abrupt GUI death while asserting a single unchanged adapter GUID. x86 runs under WOW64; native 32-bit Windows hardware is not tested. There is no whole-machine kill switch. No credentials are bundled.
