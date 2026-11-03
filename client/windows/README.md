# Personal VPN for Windows — 0.6.2 preview

Download the x86 (32-bit) or x64 Windows Setup EXE from the private GitHub release.
Windows 10 build 19041 or later is required. Setup and the application require Administrator. Setup includes the self-contained
.NET runtime, architecture-matched Xray / Hysteria / sing-box, and signed Wintun.
No local compilation or separate driver download is needed. Setup prepares the
driver and an automatic Windows service that holds one reusable adapter without installing tunnel routes. The ZIP also includes an elevated
install.ps1. Disconnect an active TUN session before upgrading its adapter service.

Home has server profiles, Proxy / TUN, the VPN protocol selector and live traffic.
X and Minimize hide to tray. Left-click the tray icon or reopen the shortcut to
restore the window. Disconnect & exit restores owned proxy settings and releases
capture routes / DNS while retaining the adapter. The menu stays open when the shell tray panel loses focus,
and closes on outside click, Esc or selection.

Import a vpnctl Windows export folder containing xray.json / hysteria.json.
Only connection fields are imported. Credentials / settings stay in current-user
DPAPI under %LOCALAPPDATA%\PersonalVPN. Runtime files are ACL-restricted and
removed after teardown. No credentials or session logs are bundled in the release.

Proxy captures apps that follow Windows HTTP / HTTPS proxy settings. App Tunnel
is visible but disabled. Site Tunnel affects captured traffic. Connect verifies
authenticated HTTPS before owning the per-user Windows proxy. A separate proxy
watchdog conditionally restores the previous settings after a crash and never
overwrites a newer selection by another application.

TUN mode requires confirmation. Connect first verifies the native server, opens
the existing service-owned Wintun adapter, starts a capture session and
checks DNS / HTTPS through OS capture before showing Connected. Both TCP and UDP
are enabled on the native frontend. No protocol automatically falls back to Direct.
There is no whole-machine kill switch; reconnect can interrupt flows and temporarily
release capture. The protection state is shown and logged.

TUN DNS returns IPv4 addresses by default for VPS compatibility. Enable IPv6 DNS
in VPN configure only after the VPS IPv6 Internet path has been verified. This
does not disable Windows IPv6 or mesh interfaces; IPv6 address space stays in
the TUN capture plan.

App / Site rules support Tunnel, Bypass and Block defaults with per-target
exceptions. Rules off retains but ignores that module's list. Explicit App rules
win over conflicting Site rules. App default follows exceptions; when App rules
are off the Site default applies. With both modules off captured traffic tunnels.
Child/helper executables need their own entries. Private health and protocol core
traffic have reserved control rules to keep verification and transport working.

Existing ZeroTier / Tailscale inner address prefixes are excluded from TUN routing.
Their outer transport follows app rules. Mesh peers still require healthy service,
membership authorization and OS firewall rules; merely excluding routes is not a
proof of peer reachability. Setup never removes other applications' adapters.

Logs show backend states, reconnects, failures and results only. The UI has Clear
and explicit Export with internal scrolling. The memory ring is limited to 1,000
records and 1 MiB; no limit counters or explanatory banner are shown. Hiding to
tray retains the session; Exit and reopen clears it. No automatic disk logging.

Traffic shows tunnel payload counters, rates and a bounded 60-sample history.
Direct traffic is excluded; health traffic is included. These are not billing
totals. Reconnect retains session totals and marks unavailable counters explicitly.

Native downloads are checksum pinned. CI builds a pinned sing-box source revision
with an OpenAdapter-only patch and embeds its resulting hash in the application. Wintun's vendor
signature is checked during packaging. No shared Wintun driver is uninstalled.
The independent guard resets only the owned interface routes, addresses and DNS
on normal stop or parent exit. It never deletes the persistent adapter.
The service creates the device only if missing. Disconnect, reconnect and mode
changes reuse its GUID. TUN suspends manual / PAC proxy capture and conditionally
restores the previous snapshot on exit, excluding a stale owned loopback proxy.

CI builds both architectures, tests native Proxy restoration, real TUN capture,
UDP forwarding, executable policy priority and abrupt-parent cleanup on isolated
Windows x64 runners, then compiles / installs the Setup EXE before release.
x86 is exercised under WOW64; native 32-bit Windows hardware is not tested.
