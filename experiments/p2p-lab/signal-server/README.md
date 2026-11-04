# P2P signaling sidecar

The primary installer is `P2P-VPS-INSTALL.sh` in the qualified Actions bundle.
It pulls the qualified private GHCR image by digest, then starts one independent
container in `/opt/personal-vpn-p2p-lab`. Log in to GHCR once on the VPS with
`docker login ghcr.io -u kittawat-thongpud`, entering a classic token with only
`read:packages` at the Password prompt. No token is embedded in the installer.
The repository and image remain private. GitHub Actions builds/publishes;
neither the Windows computer nor VPS builds the image.

The Windows helper `client/tools/prepare-p2p-vps.ps1 -Copy` downloads/checks
the small installer from a successful run and copies the permanent script.
It includes no expiring artifact URL. Running the installer again retains the
controller certificate, enrolled devices and unchanged container.

The legacy `install.sh` / image archive are retained only as a CI artifact
fallback. Never extract either installer into `/opt/personal-vpn` or merge its
Compose project into the Internet VPN project.

- New endpoint: `https://45.77.179.227:9443` (TCP 9443).
- Existing TCP 443, UDP 443, TCP 80 and the REALITY test container are untouched.
- Limits: 0.20 CPU, 64 MiB, 64 PIDs, 2 MiB Docker logs, 16 rooms / 2 peers per room,
  10-minute pairing expiry, 256 KiB signaling per room. These are engineering caps,
  not measured peak-load headroom. HTTPS polling is signaling, never RDP/SSH relay.
- Independent EC certificate/key and device database live in `signal-state`.
  Clients pin the exact certificate and still check its validity and IP identity.
  No global certificate-trust or existing VPN certificate/private-key changes.
- Copy the initial **private owner enrollment** from the VPS console once into
  Windows P2P Lab. The owner then creates a room/one-use guest QR. Redmi scans it
  to enroll its own device credential and join automatically. Future joins can
  use an eight-digit room code when already enrolled with this controller.
- Device credentials stay in Android Keystore-encrypted / Windows DPAPI-encrypted
  storage. QR tickets expire and cannot be reused. Never post enrollment strings,
  controller databases/private keys or client encrypted profiles in chat/issues.
- The controller sees ephemeral ICE/SDP metadata. Test payloads remain DTLS
  encrypted between clients. There is no TURN, IP forwarding or exit-node mode.
- One active pairing room per owner device; creating another retires its old
  signaling room and invitation, without affecting an established data channel.
  Controller failure/expiry must not close a working peer data channel. Rooms
  are in memory and expire on restart; enrolled device credentials persist.

No Internet VPN downtime is required for this sidecar. There is still shared
host/network risk, and native HTTPS polling adds a small workload; inspect
`docker stats --no-stream` with the existing VPN active after deployment.
To roll back: `docker compose -f compose.signal.yaml down` **in this directory only**. Keep
`signal-state` if you intend to retain the controller identity and devices.

On university Wi-Fi with both endpoints using a VPN, successful rendezvous does
not prove that direct UDP is possible. Client isolation, VPN capture, firewall,
mDNS restrictions or STUN reachability remain separate diagnostic questions.
