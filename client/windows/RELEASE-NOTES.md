Windows 0.6.0: persistent TUN adapter lifecycle.

Setup installs the signed Wintun driver and a small automatic Windows service. The service holds one PersonalVPN adapter. Connect, Disconnect, reconnect and Proxy / TUN switches reuse that device; they reset session routes, addresses and DNS rather than deleting and recreating adapters. The TUN core is built in GitHub Actions from a pinned source revision with an OpenAdapter-only patch.

TUN temporarily suspends Windows manual / PAC proxy capture, preventing traffic from being sent to a stale local proxy. Disconnect conditionally restores previous settings, with stale owned loopback settings normalized to Direct. Captured HTTPS must match the authenticated server exit before Connected. Faulted workers no longer prevent a subsequent reconnect or mode change.

x86 (32-bit) and x64 installers and portable ZIPs require Administrator. The protocol selector remains on Home, App exceptions precede Site exceptions, backend logs retain their session bounds without UI counters, and tray behavior is preserved. Existing mesh inner prefixes remain excluded.

CI tests real capture, UDP, app rules, repeated Proxy / TUN / Disconnect cycles, core crash recovery, stale proxy recovery and abrupt GUI death while asserting a single unchanged adapter GUID. x86 runs under WOW64; native 32-bit Windows hardware is not tested. There is no whole-machine kill switch. No credentials are bundled.
