# VPS diagnostic result

2026-11-02, client tests on Windows against the operator's VPS `45.77.179.227`.
No Windows system proxy, DNS, route, firewall or existing VPN configuration was
changed. Only owned loopback core processes were started, then stopped.

| Test | Observed result |
| --- | --- |
| TCP443 through the existing localhost HTTP proxy | TLS1.3, verified certificate for the configured REALITY fallback target, ALPN h2. This was an unauthenticated fallback check only. |
| SSH22 through the existing proxy | SSH banner and host fingerprint matched the fingerprint read in the Vultr console. |
| VLESS + REALITY + Vision, using the existing HTTP proxy as transport upstream | Authenticated HTTPS exit check returned `45.77.179.227`; four parallel exit checks also matched. |
| VLESS + REALITY + Vision, using the existing OS route without the HTTP proxy | Connection reset. The cause was not established. |
| Hysteria 2, using the existing OS route | Authenticated HTTPS exit check returned `45.77.179.227`; four parallel checks also matched. The exported certificate pin was enforced. |
| Invalid VLESS UUID, through the existing proxy | Request failed. The valid profile succeeded again afterward. |
| Incorrect Hysteria certificate pin | Connection rejected. The valid pinned profile succeeded again afterward. |
| Incorrect Hysteria password | Connection rejected. The valid profile succeeded again afterward. |

The exit check used small HTTPS responses, not a throughput benchmark. Chained
REALITY success demonstrates server/profile interoperability through the existing
proxy; it does not prove independent access from China. Hysteria's existing-OS-
route result also does not establish whether another VPN/TUN was underneath that
route. No claim of invisibility, roaming recovery, mesh compatibility, automatic
failover, packet-leak protection or production GUI integration follows from these
checks. The distributed Windows client has not yet been implemented.

The restricted profile-fetch SSH key was revoked through the console after use.
Both local test-key files were removed. Only device client exports remain in the
private ignored directory. Raw native core logs and credential contents were not
included in this record.
