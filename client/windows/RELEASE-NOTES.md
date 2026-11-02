Windows x86 (32-bit) and x64 Setup installers plus portable ZIP packages. Administrator access is required for the app. Setup includes verified native protocol cores, sing-box and signed Wintun and prepares the driver.

Home has the protocol selector below Proxy / TUN. Logs retain their internal bounds with no descriptions or size counters. The tray menu remains open when the shell overflow panel loses focus and closes on selection, outside click or Esc.

TUN captures apps and services with App exceptions before Site exceptions. A separate guard tracks the core-created adapter and removes it on disconnect / parent exit, including crash recovery. Existing ZeroTier and Tailscale inner network prefixes are excluded from capture. The outer transport follows the chosen App policy. Mesh membership and peer reachability still require a healthy mesh service.

TUN is activated only after mode confirmation and Connect. Authentication and HTTPS verification precede capture, and captured DNS / HTTPS must verify before Connected. There is no whole-machine kill switch. Reconnect can interrupt flows. No credentials are bundled.

Built and checked by GitHub Actions, including actual adapter, UDP relay, process rules and crash cleanup on isolated Windows x64 runners. The x86 build runs under WOW64 in CI; native 32-bit Windows hardware is not tested.