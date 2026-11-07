# LAN / Wi-Fi egress and recovery

The Windows 0.9.1 candidate addresses the LAN captive-portal reproduction on
2026-11-07. With 0.9.0, the existing Site Bypass rule for 10.184.6.32 timed out;
a temporary /32 route via the Ethernet gateway returned HTTP 200; removing that
route restored the timeout. The route was removed. That test establishes a path
problem, not who originally enabled Ethernet forwarding or completed sign-in.

## Changes

- Native Direct sockets select the longest matching destination prefix on an
  eligible, connected Ethernet/Wi-Fi interface, then the route/interface metric.
  The socket controller no longer binds every destination to one default NIC.
  A forwarding/weak-host conflict fails visibly rather than recursively entering
  capture. Selection does not install OS Site Bypass routes; App rules retain
  precedence over Site rules.
- Native TUN no longer changes WeakHostSend on physical/mesh interfaces. An
  independent guard owns a durable, flushed GUID/family journal for the two
  strong-host flags it needs. Writes use a fresh complete IP-interface row;
  restoration changes only leased fields still equal to the applied value.
  DNS, DHCP, MTU, metrics, weak-host receive and unrelated properties are preserved.
- Forwarding changes are refused when RRAS is active or the specific connection
  has ICS sharing enabled. SharedAccess service state alone is insufficient:
  adapter inspection can start that service without enabling sharing. ICS state
  uses read-only INetSharingManager/INetSharingConfiguration properties.
- The guard prepares hot-plugged links, resets capture on owner death, and leaves
  a journal for next-start recovery if the guard also dies. An absent USB NIC
  defers its flag restoration; only recovery metadata survives, not credentials.
- Reset inspects routes, IPs and DNS even when an address is already gone. An
  empty/idle adapter needs no DNS write. Ownership mismatches and read errors do
  not pass as successful cleanup. Teardown steps run independently; failure
  retains recovery data and prevents a fresh capture until recovery succeeds.
- Windows proxy restoration remains conditional on ownership and is read back
  before its recovery record is removed. Active sessions poll physical metrics
  alongside address/availability events. ZeroTier/Tailscale interfaces and their
  existing capture exclusions are unchanged. The VPS is unchanged.

## Validation status on 2026-11-08

- Windows PowerShell 5.1 mock tests pass all six cases: idle/missing DNS row,
  orphan route without an address, address-only, DNS-only, access denied, and
  ownership mismatch. These tests perform no host networking writes.
- Exact native source selection/eligibility tests passed on x86 and x64 Actions
  runners: LAN-only, Wi-Fi-only, both defaults, destination-specific subnets,
  IPv6, metric changes, no path, unsafe binding and mesh/capture exclusions.
- Earlier candidate builds passed normal and disk-journal recovery with full
  stable native properties plus DHCP/DNS unchanged, on both architectures.
- The first crash-child runs exposed an overbroad service-state conflict check.
  The per-connection sharing fix is committed; its final managed build, live
  crash tests and external Direct HTTPS gate are **not yet qualified**.
- [Latest qualification](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42390447318)
  did not start: GitHub reports failed account payments or a spending-limit issue.
  No new stable release, installer installation or live-host TUN switch has run.
  Client 0.9.0 remains the stable release until the complete gates pass.

After Actions is available, run Client release on the current
`codex/fix-windows-egress-recovery` head. The installer must pass x86/x64 native
Proxy, physical recovery, real captured HTTPS/DNS/UDP, external Direct HTTPS,
App priority, repeated mode switches, GUI/guard death, and upgrade/reinstall
checks. Android retains the shared client version and must pass its release gates.
Native source-matched build caching skips no downstream release gates.

For workstation acceptance, keep a separate VPN available, finish the old
Personal VPN session, install the qualified build, and check the university
portal with no temporary host route. Test both transports, LAN-only, Wi-Fi-only,
handover, Disconnect and application exit. Compare the pre-install snapshot in
`.cache/lan-audit/before-client-0.9.1.json`. No historical physical values without
a recorded baseline should be guessed or reset globally.

## References

- [Pinned native binding conflict](https://github.com/SagerNet/sing-tun/blob/040db71fcd33/tun_bind_windows.go)
- [Read-modify-write IP interface properties](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-setipinterfaceentry)
- [Per-connection sharing configuration](https://learn.microsoft.com/en-us/windows/win32/api/netcon/nn-netcon-inetsharingconfiguration)
