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

Current validation (0.6.2): both architecture matrices, native TUN integration
and Setup upgrades passed in Actions run 41581428268. The x86 package is installed
on the development host. Both REALITY and Hysteria 2 captured HTTPS through
45.77.179.227, and repeated TUN / Proxy / TUN / Disconnect cycles retained one
adapter GUID. The final GUI session remains connected using TUN / Hysteria 2;
ordinary no-proxy curl reaches Google, and both ZeroTier self and peer ping pass.

Live testing found a second issue beyond adapter ownership: the upstream IPv6
path did not work for apps selecting DNS AAAA results. Version 0.6.2 defaults
TUN DNS to IPv4, with an explicit opt-in for working VPS IPv6. This does not
disable Windows or mesh IPv6. Literal IPv6 still needs upstream support.

The first 0.6.2 installed service bootstrap failed readiness; a retry of the
installed adapter preparation succeeded. Its cause was not established and is
recorded separately from the subsequent successful live tests. See VALIDATION.md
for evidence and limits, including the transient peer-ping timeout and WOW64
rather than physical 32-bit coverage.
