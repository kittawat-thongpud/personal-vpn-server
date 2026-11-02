# UI preview validation

2026-11-02, Windows host, installed Chrome in a separate headless profile.
Only the local preview was exercised. The user's browser/VPN was not controlled.

Verified interactions:

- Protocol selection updates transport labels and limitations.
- Simulated Hysteria UDP failure enters Blocked; a pin mismatch also enters
  Blocked. No simulated Direct fallback is selected for protected traffic.
- Disconnect cancels a pending recovery attempt; stale timers do not reconnect.
- While Connected, changing settings preserves the active selection until
  Apply & reconnect. The connection state remains visibly simulated.
- Domain suffix matching respects label boundaries. IPv4/IPv6 sample mesh
  destinations retain the mesh decision. App-path matches apply in TUN preview
  and are not presented as whole-machine protection in Proxy preview.
- IPv4/IPv6 CIDR rules, invalid URL rejection, user-rule ordering and explicit
  Block precedence were exercised through the UI.
- JSON export displays parseable session events for copying inside the sandbox,
  without relying on a download or clipboard API.
- All three pages in both layouts were checked at 320, 736 and 1040 pixel viewports
  with light and dark appearance: 36 combinations, no horizontal document overflow.
- No JavaScript page errors were observed during these checks.

The browser renderer supplies icons and layout controls. The application fragment
contains no fetch, XHR or WebSocket calls; URL constructors parse input locally.
No service, network adapter, system proxy, route, DNS or firewall configuration
was installed or changed. No protocol authentication, real recovery, throughput,
packet-leak protection or mesh-product compatibility is established by these
tests. The production client must pass the separate Windows design acceptance
criteria on an isolated test machine.

Protocol/platform references used for the on-screen guidance:

- [Mixed proxy inbound](https://sing-box.sagernet.org/configuration/inbound/mixed/)
- [TUN and Windows strict routing](https://sing-box.sagernet.org/configuration/inbound/tun/)
- [REALITY](https://github.com/XTLS/REALITY)
- [Hysteria congestion and server configuration](https://hysteria.network/docs/advanced/Full-Server-Config/)
- [Tailscale coexistence limits](https://tailscale.com/docs/reference/faq/other-vpns)
