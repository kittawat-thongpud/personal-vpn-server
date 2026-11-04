# Isolated P2P Lab qualification

Date: 2026-11-04 (Asia/Shanghai)

Experiment source: `cccf09275aad35d154d0284cd6f4eb251ca10603` on `codex/p2p-lab`.
Stable Client 0.7.1 / server source, installed clients and VPS configuration are
unchanged. This is an experiment with Actions artifacts, not a stable release.

CI: https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41842452968

All workflow gates passed. Qualified Actions artifact (retained for 7 days):
https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41842452968/artifacts/13330370664

| Check | Result |
| --- | --- |
| Node input, identity, selected-path, log-bound and cancellation checks | 6 passed |
| Chromium real peers, Linux | 2 passed; 1 MiB verified each direction |
| Chromium real peers, Windows Server 2022 | 2 passed; 1 MiB verified each direction |
| Android 14 WebView peers | 1 instrumentation test passed (23.668 s); 1 MiB verified each direction |
| Signed separate Android installer | Built in Actions; APK v2 signature verified |
| Windows/Linux HTML consistency | Byte-for-byte identical |
| Downloaded qualified bundle | Every SHA256SUMS entry verified locally |
| Real Windows and Redmi pair | Not performed |
| China Wi-Fi/cellular/cross-border reachability | Not performed |
| Physical-network bypass with the production VPN active | Not proven |
| Android screenshot/UI interaction qualification | Pending real device; CI screenshot was obscured by emulator System UI ANR |

The emulator's retained logs identify ANRs in `com.android.systemui` and Google
Play services, not the lab package. The lab's real WebView engine still completed
the identity/data/close test. The screenshot is not accepted as visual approval:
Redmi UI interaction, copy/file pickers and active-VPN coexistence need a field
trial. Desktop Chromium screenshots were inspected and show the test dashboard.

The test compares certificate/session-bound pairing codes and requires both
peers' confirmation before payload testing. Selected ICE stats exclude TURN,
without inferring physical bypass. An unresolved platform createOffer operation
is independently cancelled on Stop; selected-path checks do not accept a stale
successful/nominated pair. Pending timers and transfer buffers are released.
Exports omit SDP, ICE credentials, endpoint addresses and test payloads.

The Android app has a distinct package (`dev.personalvpn.p2plab`), no registered
service/VpnService, no capture adapter and no background-survival guarantee.
Its active VPN may still carry its sockets. The lab provides data transfer and
RTT tests; group IPs, SSH/RDP forwarding, automatic signaling/recovery and native
VPN socket protection remain future work.

Builds/packages are produced in Actions. The qualified bundle is published only
after Windows, Linux and Android gates pass and both desktop HTML builds match.
Existing client/server releases, Latest and snapshot refs are not changed.

Real-device instructions: [P2P Lab README](../experiments/p2p-lab/README.md).
Production mesh constraints: [Networks group design](NETWORK-GROUPS-DESIGN.md).
