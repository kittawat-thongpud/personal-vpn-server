# Opt-in Windows diagnostic tools

These helpers are for the current development VPS, not the distributed Windows
client. The client UI remains a simulation. The tools do not register a service,
change system proxy, install TUN, edit routes/DNS/firewall, or stop another VPN.

`http_connect.py` forwards SSH through the existing localhost HTTP proxy. It is
limited to the selected test VPS's SSH port. `fetch-test-profile.ps1` verifies the
public host fingerprint before an interactive password-based SCP download. The
password is entered only in the user's terminal, never as an argument or in chat.
The process-only PowerShell execution-policy option was used after Windows
blocked unsigned script execution; no machine/user policy was changed.

For the completed test, password authentication was rejected. A locally generated
key with a forced read-only profile-export command was provisioned through the
Vultr console. `fetch_profile_with_key.py` used that restricted key and verified
the archive allowlist before extracting three client files. The server-side key
entry was subsequently removed by the operator, and both local test-key files
were deleted. That one-off SSH key is no longer available for reuse.

Private exports are held only under `.state/client-test/45.77.179.227/windows`.
Their parent directory was restricted to the current Windows user and SYSTEM.
No profile content, server state, server TLS private key or password is in Git.
Do not paste profile JSON or links into chat. They contain device credentials.

`probe_vps.py` uses checksum-verified upstream Xray 26.10.31 and Hysteria 2.13.0
Windows binaries cached under `.cache/vps-probe/binaries`. It starts owned native
processes on random loopback SOCKS ports, tests HTTPS exit identity and four
parallel requests, and terminates its processes. Per-run configs are private and
removed after the test. Core output is redacted and bounded in RAM to 1,000 lines
and 1 MiB, without persistent event logs.

The REALITY chained test uses the current localhost HTTP proxy as the transport
upstream. The other tests use the existing OS route without modifying it. Neither
test establishes which physical/VPN route is underneath the OS default, nor does
it demonstrate Wi-Fi/cellular roaming, DNS-leak protection or a TUN kill switch.
See `VPS-VALIDATION.md` for the observed results.
