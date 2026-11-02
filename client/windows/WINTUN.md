# Windows TUN runtime — 0.5.0

The application manifest requires Administrator, as requested. The setup EXE is
built for x86 and x64. It includes architecture-matched self-contained .NET,
native Xray / Hysteria, sing-box 1.14.4 and the official signed Wintun 0.14.1.
All archive and runtime hashes are pinned; Wintun signature verification runs
in packaging. Native device cleanup on WOW64 uses the native Windows PnP utility
for the exact matched device instance, not a shell command or a wildcard.

## Connection lifecycle

Mode changes require confirmation. A TUN connection verifies native authenticated
HTTPS first, resolves transport addresses for route exclusions, prepares an
independent recovery guard and starts the TUN core. The core is the sole adapter
creator. Captured DNS / HTTPS must verify before Connected. TCP and UDP are
enabled on the native SOCKS frontend; Proxy mode retains its existing behavior.

The adapter name is scoped to the current user. Its native GUID is observed and
recorded in the ACL-protected runtime child. The guard validates parent PID,
creation time and executable identity and removes only the recorded GUID / name
on parent exit. Startup recovery checks the same ownership. A graceful console
interrupt lets the TUN core release its own routes, addresses, WFP session and
device before forced termination is considered. A scoped native device fallback
handles non-present devices. Teardown errors are logged and produce Error.

DNS settings are applied on the owned interface. Physical interface settings are
not rewritten. Disconnect releases the owned adapter and conditional proxy lease.
The shared Wintun driver is not uninstalled. Reconnect can temporarily release
capture; this preview does not implement a whole-machine kill switch.

## Routing contract

1. Native transport addresses are excluded to prevent recursion. The owned
   protocol engines use reserved Direct rules; private health uses Tunnel.
2. Explicit App exceptions precede explicit Site exceptions. Windows executable
   paths use escaped, anchored, case-insensitive matching. Child executables need
   their own entries.
3. Site Block exceptions precede other Site exceptions.
4. App default applies when enabled; otherwise Site default.
5. With both modules off, captured traffic tunnels.

Current ZeroTier / Tailscale inner address prefixes are excluded from capture.
Outer mesh transport follows the chosen App policy. Mesh service health,
membership authorization and peer firewall configuration remain separate.

## Verification

Both architectures are exercised on isolated Windows x64 CI runners. Tests create
a real adapter, verify captured HTTPS, relay UDP through Xray / Hysteria, block a
separate executable, verify App Tunnel overrides Site Block, stop and check adapter
removal / unchanged physical DNS, then abruptly kill a parent to exercise recovery.
The setup EXE is compiled and installed silently, including driver preparation.
The x86 app runs under WOW64; physical 32-bit Windows hardware is not tested.

Upstream sources:
- https://sing-box.sagernet.org/configuration/inbound/tun/
- https://sing-box.sagernet.org/configuration/route/rule/
- https://github.com/SagerNet/sing-box/tree/v1.14.4
- https://www.wintun.net/
- https://github.com/WireGuard/wintun
