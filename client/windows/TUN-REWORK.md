# Persistent TUN rework

Requested outcome: keep one installed adapter, reuse its GUID across Connect,
Disconnect, reconnect and Proxy/TUN switches; clean capture state without
recreating/removing the device on ordinary operations.

Evidence from 0.5.0:
- CI x86/x64 passed native TUN, UDP and rule tests, but user-side repeated mode
  changes produced adapter-cleanup errors.
- No-proxy HTTPS reached VPS 45.77.179.227 while TUN was active.
- Windows manual proxy still pointed at 127.0.0.1:17890 in TUN mode, so browser
  routing could bypass app rules or fail through a stale loopback proxy.
- ZeroTier inner routes and ping recovered and were verified; no ZT repair was run.

Implemented in 0.6.0:
1. Separate persistent device ownership (small Windows adapter service) from
   per-connection protocol/TUN processes. Open the existing device before Create.
2. Build the minimal pinned sing-box source in Actions with a reviewed Windows
   OpenAdapter-only patch; embed the built binary hash in the .NET assembly.
3. Reset only owned routes, addresses and DNS on stop/error. Do not uninstall the
   adapter or shared driver during normal use.
4. Own a temporary Direct Windows-proxy lease during TUN and restore it safely.
5. Always clear canceled/faulted sessions; serialize mode and connect operations.
6. Test repeat cycles, core crash recovery, Proxy/TUN transitions, a stale
   system proxy and constant adapter GUID/count on x86 and x64.
7. Release 0.6.0 installers; migrate only legacy PersonalVPN state. Preserve all
   other VPN and mesh adapters and validate actual egress + ZeroTier.

All builds stay in GitHub Actions. Do not activate test routes on the user's
working host before the new runtime is qualified.

Validation: both architecture matrices and Setup upgrades passed in Actions run
41499328725. The host update was canceled at UAC, so the old connection remains
active and host-side 0.6.0 TUN validation is still pending.
