# Windows Wintun module

The first implementation in 0.3.2 is `app/TunPlan.cs`: a deterministic compiler
from the same persisted App / Site policies to a Windows TUN routing plan. CI
validates its output with the official, checksum-pinned sing-box 1.14.4 `check`
command. It does not run an adapter or install a driver. No TUN core or driver
is shipped or activated by this release.

The UI can already store full `.exe` paths and per-target Tunnel / Bypass / Block
actions. App drafts never alter Proxy routing or reconnect an active Proxy tunnel.
Site policies are active for captured Proxy traffic now.

## Routing contract

1. Protect control traffic: explicit VPN transport IP route exclusions, direct
   rules for the owned native core executables, and DNS handling.
2. Explicit App exceptions, matching full process paths. These win over Site
   exceptions, including an explicit Site Block.
3. Explicit Site exceptions; Block exceptions precede other Site exceptions.
4. App default when App rules are enabled; otherwise Site default.
5. If both policy modules are off, Tunnel is the final action.

Defaults never precede exceptions, so a Tunnel exception can override a Block
default. Turning a module off retains but ignores that module's exceptions. If
App rules are off, Site rules still apply. An unknown process uses defaults; an
executable's child/helper processes require separate explicit entries.

The plan uses dual-stack TUN addresses, strict routing, sniffing and DNS over
HTTPS through the tunnel. Transport hosts must be resolved before creating the
plan, with explicit IPv4/IPv6 route exclusions to prevent a recursive tunnel.
The native Hysteria and Xray engines retain ownership of authentication and
full-certificate pin verification; TUN forwards to their local SOCKS frontend.

## Next implementation: elevated Windows adapter host

The normal UI must stay unprivileged. A small elevated helper will own Wintun,
routes, DNS interception and supervised TUN core lifetime, with authenticated
current-user IPC, cancellation and a parent-death watchdog. Elevation is only
requested for an explicit TUN connection. Proxy mode keeps its existing path.

Before enabling TUN in the product, qualify all of these on an isolated Windows
machine rather than the development host used to communicate from China:

- Native authenticated TCP AND UDP forwarding, both protocols, and DNS policy.
  The existing Proxy frontend intentionally disables UDP; it must gain a separate
  verified TUN path before this plan can be used.
- Driver signature / binary integrity; no shell interpolation from policy data.
- Exact route/DNS/firewall restoration on disconnect, helper crash and UI crash.
- No Direct fallback during authentication failure or reconnect; explicit Bypass
  remains available according to policy. A clear protection-state indicator is
  required; `Connected` cannot mean only that the adapter exists.
- Actual per-process identity, child processes, domain/IP precedence and blocked
  defaults with exceptions; IPv4 and IPv6 leak tests.
- Wi-Fi/cellular change, sleep/resume, adapter replacement, occupied ports,
  competing VPNs and recovery after a killed helper/core.
- Mesh address and DNS ownership for Tailscale / ZeroTier, including their outer
  transport. Existing mesh routes alone are not proof of qualified coexistence.

Upstream references:
[TUN](https://sing-box.sagernet.org/configuration/inbound/tun/),
[process-path rules](https://sing-box.sagernet.org/configuration/route/rule/),
[routing actions](https://sing-box.sagernet.org/configuration/route/rule_action/),
[Wintun](https://www.wintun.net/).
