# Validation record

## Deployment regression checks (2026-11-02)

- The operator's VPS inspection showed correct read-only binds to `/etc/vpn`
  but an empty host runtime directory and no `.state/server.json`. Initialization
  and device creation generated the missing files. Subsequent operator output
  showed Xray and Hysteria running and reading their configs.
- The selected trial REALITY target was tested from that VPS with TLS1.3 and
  HTTP/2. This does not establish client connectivity from China.
- v0.1.1 adds `check-deployment` and `start`, anchored to the deployment directory,
  checking required files, GID permissions, normalized Compose mounts, Xray's
  config test inside its image, and actual running-container mounts.
- Local regression run: 18 tests, 17 passed and one POSIX-only permission test
  skipped on Windows. The workflow runs the same permission test on Linux.
- The container smoke test now extracts the deployment bundle, retains its
  relative config bind mounts, starts it from another cwd and checks that missing
  state/config is reported before startup. Linux runtime results must be checked
  on the corresponding Actions run before using the new release.

## Published Linux validation (2026-11-01)

The [v0.1.0 Actions run](https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41319981583)
completed both verify and publish. It passed 15 unit tests, native Linux protocol
integration and scratch-container startup/volume checks, then published GHCR
images and the complete private release. That container test rewrote the bind
source to an absolute temporary path; it did not exercise the extracted bundle.

## Historical local checks (2026-10-30)

Host: Windows x64. These initial local checks predated the published images and
Linux CI results above. No VPS was modified from this host.

## GitHub release pipeline preparation (2026-11-01)

- 15 local unit tests passed, including release file allowlists, exact image digest
  references, no-build Compose modes and actual archive checksums.
- Official actionlint1.7.12 accepted `.github/workflows/release.yml`.
- Both generated release Compose modes passed the official Docker Compose parser.
- Docker installation helper passed `sh -n`; it was not applied to this Windows host.
- Local source packaging excludes `.state`, runtime, exports, caches and tools.
- Workflow adds actual Linux native integration and container startup/volume tests
  before image publication. These are prepared checks, not a completed CI run.
  Release/image publication must be verified from the first real GitHub Actions run.

## Completed checks

- 12 Python unittest cases passed: independent credentials, revocation, immutable
  duplicate/reinit operations, path input validation, exact TLS fingerprint export,
  certificate rotation, ACME HTTP mode, congestion defaults, obfuscation export,
  secret-free CLI output, empty-device authentication, mapped IPv6 regression.
- The official checksum-verified Docker Compose v5.6.1 parser accepted compose.yaml
  with and without profile `hy2`. Expanded configuration confirmed separate TCP443
  / UDP443 mappings, nonroot UID/GID10001, all capabilities dropped, read-only
  runtime mount and root filesystem, namespace-only low-port sysctl, and persistent
  Hysteria state volume. Default profile contains Xray only.
- `sh -n scripts/host-tuning.sh` passed. Apply mode was not run.
- The Alpine3.22.2 index digest in Dockerfile was verified against the Docker
  Registry and includes linux/amd64 and linux/arm64.
- Native integration uses exact pinned official Xray26.10.29 and Hysteria2.13.0
  Windows binaries, checking SHA256 on download and every cache reuse. Xray archive
  is re-extracted from the verified ZIP before execution.
- Generated Xray configuration was accepted by `xray run -test`.
- Production private-address egress blocks rejected loopback TCP and UDP fixtures
  on both protocols before any test-only exception was applied.
- Authenticated SOCKS5 TCP transfers succeeded through both native protocols,
  including eight concurrent payload transfers on each. No speed/latency claim
  is derived from loopback transfers.
- SOCKS5 UDP echo forwarding passed on both protocols.
- Incorrect Hysteria TLS certificate pin, VLESS UUID and Hysteria password were
  rejected by the native cores.
- Previously valid credentials were rejected after revocation and server restart
  on both protocols, while a different device remained usable.
- Both cores started successfully with no devices after initialization and again
  after revoking the final device. Hysteria uses a random unexported sentinel
  credential because it rejects an empty user map.

The local fixtures use temporary credentials. For successful local egress only,
the test adds narrowly scoped destination/port exceptions to temporary configs.
Production generator rules continue to block loopback and private destinations.
The local TLS target is a test fixture, not a recommended production REALITY target.

## Checks required on the target VPS

1. Linux Docker build and start for the VPS architecture; scratch binary runtime,
   UID10001 access to bind mounts, fresh named-volume ownership, restart behavior.
2. Actual public reachability and security-group/firewall rules for TCP443,
   UDP443 and (ACME mode only) TCP80. Check REALITY target compatibility from VPS.
3. Actual ACME issuance/renewal and volume persistence with the operator's domain;
   no live domain or CA challenge was available here.
4. Android/Windows/Linux client import compatibility, especially exact pin
   enforcement if a GUI imports the Hysteria URI. GUI app/TUN/DNS/app bypass and
   kill-switch behavior are not implemented or validated in this module.
5. From the China networks actually used: primary and UDP fallback reachability,
   DNS/IPv6 leak behavior once clients are chosen, reconnect after Wi-Fi/mobile
   change, 1/4/8 parallel downloads and uploads, and two or three devices together.
   Record latency at idle/under load, packet loss, CPU/RSS and provider egress usage.
6. Sustained throughput, resource usage and any benefit of optional host tuning
   against a before/after baseline. Shared VPS uplinks and transfer quotas remain.

No Great Firewall traversal, invisibility, automatic failover, VPS throughput,
container image size, Linux container startup, IPv6 routing or full-device TUN
claim is made from these local checks.
