# Networks groups: P2P-only first design

Date: 2026-11-04 (Asia/Shanghai)
Status: production mesh design only. The isolated P2P Lab experiment below does
not deploy a server or modify the stable Internet VPN baseline: Client 0.7.1.

## Scope and latest decisions

The current experiment must **not modify the existing VPS**. First qualify
manual offer/answer exchange and verified client-to-client data transfer using
WebRTC Data Channels, optional third-party STUN and no TURN/relay. Both a standalone
HTML file and a separate Android foreground-only WebView app use the same engine.
See `experiments/p2p-lab/README.md`. This is not full IP mesh, SSH/RDP forwarding,
automatic room signaling or proof that the underlying traffic bypasses a VPN.
Production group integration and the VPS-controller option below are deferred.

Build a private, self-hosted group network for personal devices, initially for
SSH, RDP and IP connectivity. The group owner sets network policy; members join
under that policy, inspect connectivity and can disconnect/leave their devices.

The first implementation candidate is **P2P only**. A failed direct path is
reported as `No direct path`; there is no unprotected or implicit relay fallback.
Auto and Relay-only are future owner policies, unavailable until their protected
carrier is implemented and qualified. Unsupported policies must be rejected,
not silently interpreted as another mode.

Production integration is **no-go until the client isolation and China-network
proofs below pass**. The user's preference is to abandon the feature if its risk
to the working VPN cannot be contained. This is not approval to deploy a mesh
feature to the working VPS or replace the installed stable clients.

## Control plane versus data plane

| Traffic | Candidate mechanism | VPS role |
| --- | --- | --- |
| Manual pairing in the current lab | Trusted out-of-band exchange; compare certificate/session-bound pairing codes | None |
| Future room creation, membership, public keys, policy and endpoint exchange | Authenticated HTTPS/signaling, hosting undecided | Deferred; no new VPS service |
| Endpoint discovery / NAT mapping checks | ICE; optional third-party STUN in the lab | None on our VPS |
| Internet traffic selected by existing VPN rules | Existing Hysteria 2 / REALITY | Existing Internet VPN gateway |
| Lab data when the peer path succeeds | DTLS-protected WebRTC Data Channel with verified test payload | No lab payload transit requested; underlying VPN path still unverified |
| Future full IP group data when Direct succeeds | Candidate: end-to-end WireGuard over the selected direct peer path | No group payload transit intended |
| Group data without a Direct path, first version | Connection unavailable | No relay |

WireGuard is a candidate peer encryption engine, not a replacement for Hysteria
or REALITY and not a requirement for client-to-VPS Internet VPN connections.
P2P describes the path; WireGuard describes its cryptographic transport.

## Candidate building blocks and unresolved proof

- Current lab: use the maintained browser/Android WebView WebRTC stack. No new
  cipher or handshake; compare both DTLS certificate fingerprints via a session-
  bound pairing code. No selected TURN path is accepted. Do not label this as
  physical P2P bypass: browsers can use existing VPN or mesh interfaces.
- The lab Android app has a distinct package and no VpnService. This avoids
  claiming Android's VPN slot but does not exempt its sockets from the active
  VPN. A future native integration needs scoped protect/bind operations and
  independent recovery. Current lab disconnects require a fresh manual exchange.
- Reuse a maintained userspace WireGuard implementation for peer encryption.
  Do not implement a new cipher or handshake.
- Evaluate Pion ICE/STUN for endpoint gathering, NAT traversal and path checks.
  WireGuard does not supply room management or NAT rendezvous by itself.
- Prove the integration between ICE-selected paths and WireGuard's packet/bind
  interface, including authenticated peer identity and IP ownership. Merely
  adding a static WireGuard endpoint to sing-box is not this complete system.
- The current native builds do not enable a WireGuard feature. Adding a build
  tag is not proof that the mesh lifecycle, dynamic peers or routing work.
- No version/dependency change is authorized by this design note. Pin and audit
  selected library revisions during a separate implementation experiment.

Raw WireGuard does not provide traffic camouflage. Direct UDP can fail because
of NAT, firewalls, provider restrictions or censorship. There is no guarantee
that it works on all Chinese Wi-Fi/cellular networks. P2P-only means accepting
unavailability in those cases, not forcing the network to permit the path.

## Future relay acceptance condition

Any future VPS-carried group payload must travel under an explicitly selected
Hysteria/REALITY carrier, with WireGuard encryption still end-to-end between
the group devices. No naked WireGuard listener or implicit UDP relay on the VPS.

Desired future path, not implemented:

`A -> WireGuard ciphertext -> Hysteria/REALITY -> opaque mesh relay on VPS`

`  -> protected carrier to B -> WireGuard decryption at B`

This requires a relay, packet framing and flow control, qualified UDP/stream
adapters, bounded queues, MTU handling and transport recursion protection. A
REALITY TCP carrier can introduce head-of-line blocking; it must be measured.
Both endpoint legs must meet the group's carrier policy.

Group invitations must never distribute the owner's general Internet VPN
credential. A relay needs separate, scoped device authorization and egress
restrictions. Existing TCP/UDP 443 listeners are occupied by REALITY/Hysteria;
no listener replacement or reverse proxy in front of them is part of phase one.

## Group privacy and authorization

- Per-device private keys remain in Android Keystore-backed storage / Windows
  DPAPI-protected storage. The controller stores public identities and metadata.
- Expiring, limited-use invitations and owner membership approval bind device
  identity, assigned addresses, group and permissions. Verify signed policy
  versions; do not let a member change the group's mode.
- Authenticate packets and enforce peer allowed IPs and group isolation.
- P2P peers necessarily learn each other's usable network endpoints. Encryption
  protects payload confidentiality; it does not hide public IPs from peers.
- A future Relay-only privacy mode must suppress direct candidate exchange and
  direct probing. Previously disclosed endpoints cannot be made unknown again.
- Policy enforcement assumes the official clients. It cannot stop an authorized
  malicious device from copying accessible data or using a modified client.
- Cached memberships need bounded validity. Controller loss must not break the
  Internet VPN, but instant revocation on an offline device cannot be promised.

## Client isolation requirements

### Android

Android permits one active VpnService per user/profile. A single service can host
multiple transports, but two independent VPN apps cannot own that slot together.
Do not launch the default WireGuard Android VPN service beside Personal VPN.

The existing implementation validates an Internet profile, installs default
capture routes and closes the whole native box on transport recovery. Its own
app traffic also has a reserved Internet-tunnel rule. Inserting mesh peers into
that configuration without redesign would couple mesh joins and failures to
Internet VPN reconnects.

Before integration, prove:

1. One owner and one reader for the OS TUN descriptor; no competing engines read
   the same descriptor.
2. Group-address traffic is dispatched to a mesh engine while existing Internet
   traffic retains the established Hysteria/REALITY policy.
3. Discovery and peer sockets use explicit protected physical-network paths;
   direct attempts must not be accidentally captured by the Internet VPN.
4. Mesh join/leave, peer updates and mesh recovery do not close/restart the
   Internet engine. Internet recovery does not erase mesh membership.
5. Separate settings/fingerprints prevent a room update from marking Internet
   profiles dirty or triggering Apply & reconnect.
6. A mesh-worker failure is contained. A native panic in a shared process can
   still kill both modules; logical separation alone is not process isolation.
7. Mesh-only operation does not require an Internet VPN profile or install
   Internet default routes / global DNS changes.

The sing-box userspace WireGuard endpoint is an integration candidate, not a
proven solution to these lifecycle requirements. Start with an isolated packet
transport experiment before changing the stable Android service.

### Windows and third-party meshes

Prefer an independent mesh worker and a separately owned persistent interface,
with only explicit group routes. Keep the working Internet Wintun adapter and
its service ownership intact. Never delete, reconfigure or restart ZeroTier or
Tailscale adapters/services to make the new module work.

Allocate group addresses only after conflict checks against active LAN, group,
ZeroTier and Tailscale routes, and the existing Personal VPN address ranges.
Use narrow routes; reject overlaps rather than stealing a more specific route.
Do not advertise 0.0.0.0/0, ::/0, LAN gateways, exit nodes or global DNS in v1.
Multiple competing full-tunnel/exit-node configurations are not guaranteed to
coexist merely because ordinary mesh adapters can coexist.

Existing Internet App/Site policy must remain intact. Define and test explicit
App Block behavior for owned group traffic before integration; a new interface
must not silently bypass a user prohibition. Group mode never grants access
across group ACLs or authority over the device's Internet VPN mode.

## Networks UI

Home / Networks / Settings. Networks offers Create group and Join invitation.
Group cards show name, role, assigned local group IP and connected members.
Member rows show device name, group IP, authenticated RTT, reachability and the
actual selected path (Direct LAN/IPv4/IPv6, or an explicit failure).

Owner mode is editable only by the owner; members see it read-only. Auto/Relay
controls remain unavailable while their carriers are unimplemented. Controller
Online is distinct from Peer reachable and from Direct P2P. Never show a made-up
ping or infer Direct from control-server latency. Lists scroll internally in
the existing compact layout.

Backend events share the existing session-only 1,000-record / 1 MiB client cap.
Store group membership durably and encrypted, separately from session logs.
Do not export invitations, private keys or raw authentication material in logs.

## VPS capacity assessment

User-supplied snapshot on 2026-11-04; not an agent-operated load benchmark:

| Item | Observed |
| --- | --- |
| Capacity | 1 vCPU, nominal 1 GB RAM, 25 GB SSD |
| OS memory / available | 955 MiB total / 519 MiB available |
| CPU in new vmstat samples | 91-96% idle; steal 0-1% |
| Swap | 55 MiB used; no swap-in/out during the sample |
| Root disk | 23 GiB filesystem; 7.1 GiB used; 15 GiB available |
| Xray / Hysteria RSS reported by Docker | 27.07 / 16.35 MiB |
| Existing 8443 test container RSS | 12.02 MiB |

No additional service is installed on the VPS for the current P2P Lab. If the
lab's traffic is captured by an existing VPN, it can still consume VPS traffic;
the absence of a new service alone does not prove zero data-plane load.

For a future self-hosted controller, this snapshot supports a small control-plane
experiment. Use a lean service with SQLite
and signaling, not a multi-service dashboard/database stack. An initial control
service memory target/cap around 64 MiB is an engineering budget to validate,
not a measurement or throughput guarantee. Use bounded request/connection counts
and logs. Build all binaries/images in Actions, not on the VPS.

For a qualified physical direct path, payload bandwidth and encryption run on
the clients. A future controller would handle small membership/signaling messages;
the current manual lab does not use it. No relay is deployed in v1.
If relay is considered later, its CPU, memory and aggregate bandwidth require
separate limits and a concurrent-VPN benchmark. This snapshot does not establish
maximum relay Mbps or peak-load headroom. Shared-host/network faults cannot be
eliminated through container limits alone.

## Release gates and stop conditions

1. Preserve Client 0.7.1 and existing source snapshots. Experimental development
   stays separate from the stable release and VPS listeners.
2. Prove authenticated direct data transfer with no OS default-route/DNS changes.
3. Prove Android single-TUN composition and failure isolation. If it requires
   disruptive lifecycle changes that cannot be contained, stop integration.
4. Exercise feature OFF against the existing VPN qualification. Exercise ON with
   real VPN traffic, ZeroTier/Tailscale routes, group leave, IP conflicts, worker
   crashes and owner revocation. No disabled or bypassed regression gates.
5. Test real China LAN/Wi-Fi/cellular and cross-border cases separately. Report
   P2P failures honestly; do not hide them behind an unimplemented fallback.
6. Only add Auto/Relay after proving the protected carrier, scoped credentials,
   traffic caps and sustained coexistence. Upgrade VPS capacity only if measured
   load calls for it, not because a controller theoretically needs a large host.

## Primary references

- Android VpnService constraints: https://developer.android.com/develop/connectivity/vpn
- WireGuard transport and obfuscation limits: https://www.wireguard.com/known-limitations/
- WireGuard protocol: https://www.wireguard.com/protocol/
- Userspace implementations: https://www.wireguard.com/repositories/
- Userspace endpoint candidate: https://sing-box.sagernet.org/configuration/endpoint/wireguard/
- ICE candidate library: https://github.com/pion/ice
- Current WebRTC experiment: https://www.w3.org/TR/webrtc/
- Selected-path statistics: https://www.w3.org/TR/webrtc-stats/
- Existing lifecycle: client/android/app/src/main/java/dev/personalvpn/client/TunnelService.kt
- Existing capture policy: client/android/app/src/main/java/dev/personalvpn/client/Model.kt
- Existing Windows routes: client/windows/app/TunPlan.cs and TunSupport.cs
