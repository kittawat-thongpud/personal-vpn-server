# Personal VPN for Windows — 0.3.2 preview

Real Windows x64 application, built in GitHub Actions. Unzip the Windows release
and run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\install.ps1`.
Installation is per-user, without administrator rights. Open **Personal VPN** from
the desktop / Start menu. The self-contained package includes the .NET runtime.

Import the folder containing the `vpnctl export <device>` output (`xray.json`
and/or `hysteria.json`). Imports store connection material only: no imported
listeners, routing or log-file paths. Settings and credentials are protected with
Windows DPAPI for the current user in `%LOCALAPPDATA%\PersonalVPN\settings.dpapi`.
Runtime configs need plaintext for the native cores; they are kept in an ACL
restricted per-user runtime directory and removed after disconnect / next startup.
Never share the source profiles or the application data directory.

Click **Connect** and wait for **Connected** and a verified exit IP. By default,
the application then sets the per-user Windows HTTP / HTTPS proxy to its local
listener. Browsers that follow Windows proxy settings use this path automatically.
One Connect action waits for tunnel verification and selects Personal VPN's system
proxy; there is no second proxy activation button. Home shows tunnel health and the verified exit. Configuration and diagnostics are under Settings. If another app overwrites Windows proxy settings,
Home reports **Proxy conflict** within about one second. The primary button then
offers **Use proxy** to select Personal VPN again without restarting the tunnel.
Repeated competing proxy writes require disabling the other app's automatic proxy
selection; this app does not silently fight another VPN's settings.

The previous manual proxy, bypass list, PAC URL and auto-detect flags are saved
in a DPAPI protected recovery record. Disconnect restores the snapshot only while
the current settings still belong to Personal VPN. A separate watchdog restores
after a process crash; startup also recovers abandoned ownership. Reconnect keeps
the selected local proxy in place while the core restarts. This is not a system
kill switch: an app crash restores the previous path. Another app's newer proxy
choice is retained instead of overwritten. Existing HTTP / SOCKS listeners also
support applications with their own explicit proxy settings.

The first
protocol is **Hysteria 2**; **Settings > VPN configure** also offers **REALITY**. The REALITY option can
explicitly use an existing local HTTP proxy at `127.0.0.1:7688`. That option depends
on the other VPN remaining running. Protocols never automatically switch to Direct.

Choose Hysteria 2 or REALITY in Settings > VPN configure, save, and use Connect on Home. There is no
separate REALITY test button or automatic protocol failover. A failed direct TCP
path still requires diagnosis; the protocol selector does not make it reachable.

Home shows live upload / download rates, session totals and a 60-sample chart.
Counters come from the selected Xray tunnel outbound through a loopback-only
StatsService, updated once per second. Direct / Bypass traffic is excluded.
Health traffic is included; these are payload counters rather than transport
overhead or billing totals. Session totals survive automatic core restarts, and
history survives page navigation. Stats unavailability is shown explicitly.
The polling endpoint does not expose a heap / debug service or handler controls.

Dark widget templates cover dropdowns, scrollbars, sliders, text inputs, checkboxes,
context menus and tooltips. The window caption follows the application's dark theme.

For ChatGPT / Codex desktop's native runtime, a supported client may need
`features.respect_system_proxy = true` in the user-level `.codex/config.toml` and
a client restart. This is an application-specific opt-in, independent of the proxy
listener. See the [official native system-proxy documentation](https://learn.chatgpt.com/docs/enterprise/managed-configuration#use-the-system-proxy-for-native-client-requests).
The VPN installer does not edit third-party application configuration. Browser
proxy extensions, application-specific proxy settings and existing sockets can
keep using another route; reconnect the affected app after selecting the proxy.

The mobile column stays centered when maximized. Home has no page scrollbar:
server selector, protocol summary, connect state and live traffic fit vertically.
Navigation has Home and Settings only. Configuration forms, exception lists and
backend logs use bounded inner scroll areas with slim 6-DIP scrollbars.

**Settings > VPN configure** edits the selected profile's address, port, SNI and
REALITY fingerprint without exposing or replacing authentication material. In
REALITY mode an existing HTTP upstream remains an explicit advanced option.
Save & apply reconnects only if a tunnel is already running; otherwise it saves
for the next Connect. Home has no Configure button or local-proxy card.

**Settings > Site Tunnel** has Tunnel / Bypass / Block defaults plus Rules off.
Each domain or IP/CIDR exception has its own Tunnel / Bypass / Block action.
Exceptions override the default, including a Block default. Explicit Block
exceptions precede other site exceptions. Rules off retains but ignores the list
and tunnels captured traffic. Domains include subdomains; IP rules match literal
addresses without adding local DNS lookups. HTTPS health has a private Tunnel
inbound independent of the user policy. Existing Global / Rules and category
settings migrate into this model without discarding connection credentials.

**Settings > App Tunnel** stores full executable paths and individual actions as
Wintun drafts. These rules NEVER affect Proxy routing. Child/helper executables
need their own entries. The TUN-plan compiler places explicit app exceptions
before site exceptions, as requested. When enabled in the future TUN host, the
App default follows exceptions; with App rules off the Site policy supplies the
default. With both modules off all traffic tunnels. The elevated adapter host is
not enabled in 0.3.2; see [WINTUN.md](WINTUN.md) for the implementation boundary.

**Settings > Backend logs** keeps diagnostics accessible without a top-level
Logs tab. Clear, explicit Export and the session/size limits remain unchanged.

The supervisor debounces network changes, cancels stale checks, watches owned core
processes, verifies HTTPS through the tunnel and retries with bounded backoff.
Two different HTTPS health services are tried; an outage of both services can
still produce a health failure. A failed check shows Degraded; repeated failure
restarts the cores. Existing sessions can break during reconnect and need retry.

Logs contain VPN backend states / failures / retries / results only, at most
1,000 entries and 1 MiB in memory. Clear empties the ring. Close/reopen starts a
new log session. Only an explicit Export writes logs to a file. Raw native-core
output, imported credentials and website request URLs are not logged.

This preview uses native Xray 26.10.31 and Hysteria 2.13.0, verified by pinned
checksums. Hysteria's existing full-certificate SHA-256 pin remains enforced.
The local Xray router forwards tunnel traffic to the native Hysteria SOCKS
listener when Hysteria is selected. No new server configuration is required.

This release uses **System proxy or manual app proxy**. TUN, executable interception, Windows service,
whole-machine kill switch, mesh transport tunneling and automatic protocol
failover are not implemented. Existing Tailscale / ZeroTier routes are untouched;
coexistence has not been qualified on an isolated test machine. Programs without
a configured proxy and apps that ignore Windows proxy continue to use their current route. This is not a guarantee
against leaks from an application's own proxy bypass or alternate network stack.
Closing the app stops its owned cores; a Windows kill-on-close job also cleans
up cores after an app crash. System proxy changes are reversible as described
above. No adapter / DNS / route / firewall changes are made, and no other VPN
processes are stopped. Separate browser proxy extensions can override Windows
settings; WebRTC and other UDP traffic require separate TUN/leak qualification.

Build: `.github/workflows/windows-client.yml`. Builds and packages run on GitHub;
only source is pushed from the development machine. No device secrets are in the
release. `--self-test --report <path>` runs local deterministic checks.
`--smoke-test --profile-dir <folder> --expected-exit <ip> --report <path>` is an
explicit, loopback-only integration test with a genuine profile. It never changes
Windows network settings. Do not run it concurrently with the regular app.
`--system-proxy-test --profile-dir <folder> --expected-exit <ip> --report <path>`
explicitly tests Windows proxy capture, ordinary HTTPS, restore, retry and the
crash watchdog. This test temporarily changes and restores the per-user proxy.
`--connect-hysteria` opens the GUI and connects Hysteria + Global + System proxy.
