# Personal VPN for Windows — 0.2.0 preview

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

Click **Connect** and wait for **Connected** and a verified exit IP. The first
protocol is **Hysteria 2**; **VPN** also offers **REALITY**. The REALITY option can
explicitly use an existing local HTTP proxy at `127.0.0.1:7688`. That option depends
on the other VPN remaining running. Protocols never automatically switch to Direct.

Configure an app's own HTTP proxy as `http://127.0.0.1:17890` or SOCKS5 proxy as
`127.0.0.1:17891`. **Test browser** opens a separate Edge / Chrome profile using
the HTTP proxy and shows the exit IP. It does not modify the usual browser.
Close that test window before changing proxy ports. Local listeners accept only
loopback connections; they are not authenticated against other local processes.

**Global** tunnels captured traffic by default. **Rules** defaults to Direct.
Website and literal IP/CIDR category rules support Tunnel / Bypass / Block /
Disabled; Block takes precedence. Website domains include subdomains. Explicit
rules apply in both modes. Save each category, then **Apply & reconnect**.
Health checks always use a separate Tunnel inbound, independent of user rules.
IP rules do not resolve website domains locally to guess an IP match.

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

This release is **Proxy only**. TUN, executable interception, Windows service,
whole-machine kill switch, mesh transport tunneling and automatic protocol
failover are not implemented. Existing Tailscale / ZeroTier routes are untouched;
coexistence has not been qualified on an isolated test machine. Programs without
a configured proxy continue to use their current route. This is not a guarantee
against leaks from an application's own proxy bypass or alternate network stack.
Closing the app stops its owned cores; a Windows kill-on-close job also cleans
up cores after an app crash. No system proxy / adapter / DNS / route / firewall
changes are made, and no other VPN processes are stopped.

Build: `.github/workflows/windows-client.yml`. Builds and packages run on GitHub;
only source is pushed from the development machine. No device secrets are in the
release. `--self-test --report <path>` runs local deterministic checks.
`--smoke-test --profile-dir <folder> --expected-exit <ip> --report <path>` is an
explicit, loopback-only integration test with a genuine profile. It never changes
Windows network settings. Do not run it concurrently with the regular app.
