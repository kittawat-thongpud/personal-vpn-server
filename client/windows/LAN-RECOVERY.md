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
- [Final qualification](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/42395956271)
  completed successfully at source `77340fffc7a79bcc2cbb2ec89f97b97f8c99998b`.
  Windows x86/x64 passed normal and disk-journal recovery, GUI death and loss of
  both GUI/guard, with full stable native properties plus DHCP/DNS preserved.
- Both architectures passed real captured HTTPS/DNS/UDP, external Direct HTTPS,
  App Bypass over Site Block and App Block over Site Bypass, repeated mode
  switches, and a killed capture core starting a new instance before freshly
  verified HTTPS. The persistent adapter retained its identity. Migration and
  in-place reinstall preserved settings, shortcuts and the stable install path.
- A core exit racing with cancellation is classified separately from actual
  teardown failures; it no longer falsely blocks the next connection as an
  incomplete capture reset. Actual teardown errors retain recovery records.
- Android native/JVM/lint/signature gates and Android 14 instrumentation passed
  (`OK (5 tests)`), including both protocol handovers and bulk app selection.
  The UI test invalidates stale accessibility nodes and checks each selection;
  Android production VPN behavior is unchanged.
- [Client 0.9.1](https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/client-v0.9.1)
  is stable, non-draft and Latest at publication. Its source tag and immutable
  base/release branches match the qualified source. All 11 checksum entries and
  eight manifest entries match GitHub asset digests. The downloaded x64 Setup
  also matches SHA-256 `9eb086804367f60ff48940cfb7ced262fe3bf481592383c530811e3142521b8a`.

Builds ran in GitHub Actions; source-matched native caching skipped no downstream
qualification gates. No new installer launch or live-host TUN switch has run.
LAN/Wi-Fi-only, real captive-portal and hot-plug acceptance remain pending on the
workstation; CI does not reproduce the university network.

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
