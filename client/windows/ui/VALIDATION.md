# UI preview validation

2026-11-02, Windows host, installed Chrome in a separate headless profile.
The user's browser/VPN was not controlled. These are UI checks; separate native
protocol probes are recorded in `../tools/VPS-VALIDATION.md`.

Verified interactions:

- Protocol selection updates transport labels and limitations.
- Simulated Hysteria UDP failure enters Blocked; a pin mismatch also enters
  Blocked. No simulated Direct fallback is selected for protected traffic.
- Disconnect cancels a pending recovery attempt; stale timers do not reconnect.
- While Connected, changing settings preserves the active selection until
  Apply & reconnect. The connection state remains visibly simulated.
- Profile add/edit/select, malformed-endpoint rejection and country flag updates
  were exercised. Connected selection retains the old profile until Apply.
  Active-profile deletion requires an explicit disconnect/delete confirmation.
- Profile metadata restoration preserves the selected profile and category modes,
  without restoring log events. Failed metadata persistence is visible in the UI
  and does not enter the backend log stream.
- Separate Apps/Websites/IP lists and category modes were exercised, including
  Disabled, website-before-app precedence, IPv6 CIDR Block and sample mesh routes.
- Connect's button and label were measured at exact horizontal/vertical center.
  Back and Add controls remained visible in direct-file mode without Lucide.
- UI navigation, category edits and protocol selection produced zero backend log
  entries. Simulated connection/retry/health events entered the backend stream.
- A 1,500-event burst retained exactly the newest 1,000 records. A separate
  multibyte-message burst reached the 1 MiB cap and retained 629 entries (1,047,288
  encoded bytes), within both limits. Clear reset the ring and any prepared export;
  reloading started at zero. No automatic event-log file was written.
- With 1,000 entries the log pane scrolled (52,000 pixels of content) at a bounded
  height. Its length did not grow the window. Short-window height was also checked.
- JSON export displays parseable session events for copying inside the sandbox,
  without relying on a download or clipboard API.
- Home, Profiles, Rules, VPN and Logs in both layouts were checked at 320, 736 and
  1040 pixel viewports with light/dark appearance: 60 combinations, no horizontal
  document overflow.
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
