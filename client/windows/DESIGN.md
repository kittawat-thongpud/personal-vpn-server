# Windows client design

Status: design and acceptance criteria, 2026-11-02. The native Windows application
in `app/` implements the first Proxy preview; see [runtime limits](README.md).
Windows service, TUN driver and firewall integration remain planned. The existing
server release remains independent.

The interactive [UI preview](ui/preview.html) contains local simulated states and
sample rule decisions only; it has no core, service or network integration.

**UI modes and protocol guidance**

Use the user's compact mobile references as the starting point. The native app
has a centered vertical column even when maximized. Home contains only the server
picker, protocol/policy summary, centered Connect control, verification status and
live traffic. Home never scrolls as a page. Navigation is Home / Settings. Settings
contains VPN configure, App Tunnel, Site Tunnel and Backend logs. Configuration
forms and long lists scroll inside bounded widgets; scrollbars are 6 DIP. The
window fits the Windows work area, including display scaling. Back/add controls
remain visible without an optional icon runtime.

Server selection opens a dedicated profile menu. Add, edit, select and delete
profiles independently of application releases. Choose the country/flag explicitly;
do not infer a verified location from an untested IP. A connected session retains
its active profile snapshot until Apply & reconnect. Deleting an active profile
requires an explicit disconnect/delete confirmation. With no profiles, Connect is
disabled and an Add profile action remains available.

The preview stores metadata only. The installed client must import complete
per-device connection material and keep credentials in protected Windows storage,
separate from metadata/logs. An IP alone is insufficient. A different VPS may need
a different UUID, REALITY key/SNI/short ID or Hysteria password/certificate pin.
Replacing that profile must not require rebuilding/downloading the application.

Keep three separate choices:

| Choice | Options | Meaning and limit |
| --- | --- | --- |
| Capture | Proxy / TUN | Proxy receives traffic from configured applications; TUN captures traffic using a virtual adapter. Neither label selects the VPN protocol. |
| Policy | Tunnel / Bypass / Block default; Rules off | Exceptions have their own action and precede defaults. Rules off retains but ignores that module's exceptions. Proxy policy affects captured traffic only. |
| Protocol | VLESS + REALITY + Vision / Hysteria 2 / Auto | REALITY uses TCP443; Hysteria uses UDP443. Auto is a recovery policy across protocols, with fresh verification and cooldown, not a third protocol or a bandwidth promise. |

Present the selected protocol's limitations beside the picker. REALITY requires
a compatible target and can lose throughput under TCP packet loss. Hysteria
requires usable UDP and verified TLS identity. Protocol changes may terminate
existing connections. Both can be blocked; using the same VPS IP limits fallback.
Display planned versus verified capabilities separately from connection status.

App Tunnel and Site Tunnel have separate exception lists. Each target has its
own Tunnel / Bypass / Block action, with a configurable default for unmatched
traffic. A Tunnel exception may override a Block default. Explicit App exceptions
win over Site exceptions, as selected by the user on 2026-11-02. Explicit Block
exceptions precede other Site exceptions. App rules are saved drafts while Proxy
is the only capture implementation. Turning App rules off leaves Site policy in
charge; with both modules off, all captured traffic tunnels. See [WINTUN.md](WINTUN.md)
for the compiled routing contract and the boundary before adapter activation.

Use website/domain rules as the first version's web granularity. Whole-machine
executable selection belongs to the verified TUN integration in this version;
the Proxy preview must not present it as universal application protection.
Expose a "why this route" inspector with the exact rule, action and default.

While connected, edited settings remain a draft until Apply & reconnect.
Preserve the active policy and visibly mark pending changes. A local UI success
message is not proof that new route or protection settings reached the OS.

Mesh behavior is distinct from Traffic mode. Preserve confirmed mesh peer/subnet
routes and DNS in both Rules and Global. A separate future option can route mesh
daemon outer traffic through the VPN after testing; the peer packets still use
their mesh adapter. Exit-node/default-route ownership requires separate checks.

**Accepted requirements**

- Windows first; Android and Linux follow after the policy format is stable.
- Select tunnel paths by website/domain, executable path and IP/CIDR. Routing
  correctness takes priority over UI features.
- Recover visibly after poor signal, loss, Wi-Fi changes, mobile hotspot/cellular
  changes, sleep/resume and core failure.
- Reconnection must retain authentication/certificate checks and prevent protected
  traffic from silently falling back to a direct connection.
- Keep an audit trail of user commands, state transitions, policy changes,
  recovery attempts and failures. Provide local log viewing and diagnostic export.
- Prepare for ZeroTier and Tailscale running alongside this client. Actual mesh
  configurations are not yet available; compatibility is a test target.
- Preserve the user's currently working VPN and the communication channel used
  to develop this project. No host-network mutation during initial development.

**Runtime boundaries**

The long-term candidate is a version-pinned sing-box core behind a supervisor and
a separate desktop UI. The first installed Proxy preview instead uses the native
Xray / Hysteria cores already validated against this server, preserving Hysteria's
full-certificate pin without a public-key pin conversion. The per-user supervisor
runs outside the UI thread and owns both processes; a Windows kill-on-close job
cleans up children if the application crashes. The preview closes its tunnel on
application exit. Service-backed persistence across UI closure remains planned.
The supervisor owns core lifecycle, network-change notifications, health checks,
policy compilation and local logs. The UI reports supervisor state; UI closure
must not implicitly stop the protected tunnel. A Windows service and restricted
local IPC are planned for the installed client. Privileged operations belong to
the service rather than the UI.

The policy evaluator is independent of sing-box JSON. It must produce both the
core configuration and a human-readable explanation of each decision. Preserve
this policy format when adding other operating systems.

Core/protocol support alone is not compatibility proof. Validate VLESS/REALITY/
Vision, Hysteria userpass encoding and certificate verification against this
server. The server's current Hysteria pin is a hash of the complete certificate;
do not reinterpret it as a public-key hash. Version-specific certificate trust
or pin fields must be tested, including deliberate pin mismatch.

**Connection state and recovery**

States are Stopped, Starting, WaitingForNetwork, Connecting, Verifying, Connected,
Degraded, Reconnecting, Blocked, Stopping and Error. Every transition records a
reason, attempt ID, network generation and policy generation.

- Connected requires fresh authenticated traffic through the selected outbound.
  A running process, a virtual adapter or a successful ping alone is insufficient.
- Separate link availability, core responsiveness, tunnel authentication, DNS,
  internet reachability and destination-specific errors. A failing website must
  not automatically be diagnosed as a failing VPN.
- Debounce network-change events and cancel stale probes/reconnect attempts.
  Only results for the current network/policy generation can update UI state.
- Re-evaluate the usable upstream after a network change. Avoid binding the new
  connection to a disconnected NIC or accidentally choosing our own TUN adapter.
- Retry with bounded exponential backoff and jitter. Prefer an immediate retry
  after a genuine network change; avoid restart storms under unstable signal.
- Use timeouts, cancellation and a single serialized lifecycle controller. A
  stalled core is a different failure from a process exit and needs a watchdog.
- Support REALITY and Hysteria selection. Automatic switching requires repeated
  end-to-end failures, hysteresis and fresh certificate/auth checks on the new
  outbound. Never fall back to direct for a TunnelRequired rule.
- Enter Blocked with a visible reason when protection cannot be established.
  Notify the user on prolonged failure and core/service health failures.
- Existing TCP connections may break after an address change. Recovery means
  establishing a working protected path again; seamless session migration is
  not promised. Test actual Hysteria/QUIC behavior for the pinned core version.

**Policy model and precedence**

Actions are TunnelRequired, Direct, PreserveMesh and Block. TunnelRequired remains
blocked while the tunnel is unavailable. Direct is an explicit decision visible
in the policy editor; the core must not invent a Direct fallback.

The default is explicit in each App / Site policy: Tunnel, Bypass or Block.
Rules off ignores that module's exceptions. Initial empty profiles tunnel captured
traffic. Preserve the user's previous effective default when migrating settings.

Apply these layers in order:

1. Internal control-plane rules: prevent self-capture/route loops and keep the
   configured VPS transport reachable through its intended upstream. These rules
   apply to the core's own sockets, not a broad public-IP bypass for every app.
2. PreserveMesh for confirmed mesh interfaces, managed destination routes and
   their private DNS namespaces. Do not guess all mesh ranges from a service
   name, and do not blanket-exclude all private IP space.
3. Explicit full-path App exceptions (TUN only).
4. Site exceptions for domains and IP/CIDR, with explicit Block exceptions first.
5. App default when enabled in TUN; otherwise Site default. With both modules
   off, Tunnel. Defaults follow every explicit exception.

Protected mesh rules must be shown before user rules and conflict checks must
explain why a later rule cannot override them. Changing mesh protection requires
an explicit setting. Keep the core's transport exceptions distinct from user
application bypass exceptions.

Use canonical executable paths for Windows app identity; display names alone
are not sufficient. Handle PID reuse and child/helper processes explicitly. An
unknown process must not be reported as a successful app match.

Website rules cover exact domains and explicit subdomain suffixes. Normalize
case/IDNs and reject ambiguous wildcards. Matching example.com must not also
match notexample.com. Preserve rule IDs and emit a "why this route" result for
each evaluated flow. Explain conflicts before applying a policy.

HTTPS URL paths, individual browser tabs and individual pages are outside the
first version's domain/IP routing scope. They need browser-level integration.
Browser DoH, encrypted ClientHello, cached DNS and shared CDN IPs can hide or
confuse domain attribution. Do not assume SNI sniffing always identifies a site.
Specify the DNS/domain-attribution strategy and its conservative fallback before
enabling domain-only protection in TUN mode. When attribution is unavailable,
keep protected app scopes blocked or tunneled; never label an uncertain decision
as verified domain protection.

Policy updates must validate before activation and apply as one generation.
Failure retains the last valid policy or its protection rules. Include IP/CIDR,
IPv4/IPv6 and DNS decisions; bypassed and protected DNS must follow their stated
policy. DNS resolution through Windows system services must not be mistaken for
the identity of the browser/application that initiated the query.

**Mesh coexistence**

Keep two layers distinct: packets addressed to mesh peers/subnets retain their
mesh routes; the mesh daemon's encrypted outer traffic uses a separately selected
upstream (direct, or our tunnel where tested). Do not claim that excluding mesh
destination prefixes automatically forces the mesh transport through the VPS.

- Discover actual adapters, routes, metrics and private DNS settings read-only.
  Tailscale commonly uses 100.64.0.0/10 and fd7a:115c:a1e0::/48; include advertised
  subnet routes as well. ZeroTier ranges are configurable and must be discovered.
- Select a TUN subnet after checking overlaps with LAN, current VPN, mesh and
  virtual-machine/container interfaces. Do not use Tailscale's ranges as TUN space.
- Preserve Tailscale MagicDNS/split DNS and ZeroTier-managed DNS when present.
  Windows strict-route/DNS-leak protection is not proof of mesh DNS compatibility.
- A Tailscale exit node or ZeroTier default-route override is a different mode
  with potential route/firewall ownership conflicts. Detect and report it; avoid
  promising simultaneous full-tunnel compatibility or changing another product's
  settings automatically.
- Recompute owned exclusions after mesh routes change. Detect self-routing and
  overlapping prefixes instead of repeatedly restarting the core.
- Test installed mesh products on a dedicated test machine before declaring
  compatibility. No ZeroTier/Tailscale installation is needed on the user's host
  during the initial development stage.

**Failure protection and recovery ownership**

Core routing rules alone cannot guarantee protection after a core/service crash.
TUN mode needs an OS-enforced protection design, potentially Windows Filtering
Platform rules scoped to the protected traffic and owned by this client.

Choose filter persistence and service-crash behavior deliberately. Rules that
disappear on supervisor crash can leak; persistent rules without a recovery path
can strand the user. Both crash protection and offline recovery must be proven
on a test machine before production activation. Do not implement a global
firewall block as a shortcut for selective routing.

Journal only our own route/DNS/filter changes, including previous values and
generation IDs. Cleanup removes only resources still owned by our client and
must preserve concurrent mesh/OS changes. A rollback must not restore the entire
old route table over newer network state. Provide an offline recovery tool on
the test machine that can remove our owned configuration without internet access.
Recovery must clearly describe that removing protection can allow direct traffic.

**Logging and diagnostics**

Use structured local event logs with UTC timestamps and monotonic durations,
severity, component, event ID, session/attempt IDs, network/policy generation,
command/rule ID, action, result and sanitized reason. Each accepted backend command
has a received event and an eventual completion/failure/cancellation event.

Log only VPN backend lifecycle, network changes, auth/pin failure categories,
health-check outcomes, retry/failover, backend configuration results and core
exceptions/results. Exclude UI navigation, menu clicks, metadata edits, unsubmitted
rule edits, diagnostic-export clicks and browsing/packet histories. A backend
operation triggered from the UI can log its backend request and result.

Keep the newest **1,000 records**, also capped at **1 MiB** of serialized event
data, in RAM for the current application session. Bound individual messages,
coalesce rendering and evict the oldest entries at the limits. Do not write
automatic session log files, rotate disk logs, or restore events from saved UI
state. Close/reopen starts a fresh log session. Clear empties the ring and any
prepared export, without adding a UI action record to the backend stream.
The log list has a bounded height and vertical scrolling; its length must not
grow the application window. Profile/settings persistence is independent of logs.

Store credentials separately. Exclude passwords, UUIDs/private keys, complete
profiles, authorization headers, URL query strings and packet payloads from logs
and diagnostic exports. Domains/process paths/IPs can be useful local metadata;
make their inclusion in exported diagnostics explicit and previewable.

Provide current-session viewing, visible usage limits, Clear and explicit manual
export. An export failure must be visible; credentials and full browsing histories
are excluded. The separate minimal ownership/recovery journal needed for safe OS
cleanup is operational configuration, not a persistent traffic/event log. A
continuing background service must obey the chosen UI-session logging lifetime.

**Development and acceptance sequence**

1. Implement policy evaluation, state machine, supervisor contracts and log schema
   with deterministic tests, fake clocks and fake network/core adapters. No host
   network mutation and no real credentials are required.
2. Build the Windows UI/supervisor through GitHub Actions. Test protocol adapters
   using loopback SOCKS/HTTP proxy fixtures. An opt-in dedicated browser profile
   can later test the VPS through an explicit proxy while preserving the existing
   VPN. Such a nested test does not establish independent Great Firewall traversal.
3. Test real TUN, OS protection, DNS, sleep/resume and network changes in an
   isolated Windows test machine with console access and offline recovery. The
   user's working VPN/communication machine is not the first test target.
4. Test real Tailscale and ZeroTier, separately and together, with peer IPs,
   private DNS and advertised subnets. Treat exit-node/default-route modes as
   separate tests. Only claim the combinations that passed.
5. Host rollout follows successful isolation/recovery testing and a documented
   plan for the user's existing VPN. Starting the UI must not implicitly enable
   TUN, change system proxy, stop another VPN or install network protection.

Acceptance cases include interface loss, IP change during a transfer, Wi-Fi to
hotspot/cellular switch, suspend/resume, stalled core, killed core/UI/service,
unreachable VPS, TCP-only/UDP-only failure, wrong certificate pin, failing DNS,
policy conflicts, PID reuse, concurrent policy/network updates, full log disk,
upgrades/rollback, mesh route changes and offline removal of owned resources.
Verify both positive routing decisions and the absence of protected direct
egress using packet observations on the isolated machine. A Connected label,
unit tests or successful packaging alone cannot establish those properties.

Sources (capabilities and platform constraints; the design above is a proposal):

- [sing-box TUN and Windows strict routing](https://sing-box.sagernet.org/configuration/inbound/tun/)
- [sing-box process/domain/IP routing rules](https://sing-box.sagernet.org/configuration/route/rule/)
- [sing-box outbound interface selection](https://sing-box.sagernet.org/configuration/route/)
- [sing-box TLS certificate and public-key pins](https://sing-box.sagernet.org/configuration/shared/tls/)
- [Tailscale alongside other VPNs](https://tailscale.com/docs/reference/faq/other-vpns)
- [Tailscale DNS](https://tailscale.com/docs/reference/dns-in-tailscale)
- [ZeroTier client route/DNS settings](https://docs.zerotier.com/config/)
- [Windows interface-change notifications](https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-notifyipinterfacechange)
- [Windows Filtering Platform](https://learn.microsoft.com/en-us/windows/win32/fwp/windows-filtering-platform-start-page)
