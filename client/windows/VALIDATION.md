# Installed Windows preview validation

## 0.2.3 browser capture update — 2026-11-02

The initial IP mismatch was a capture gap: Windows still used the other VPN's
proxy at `127.0.0.1:7688`. Version 0.2.0 verified tunnel health but did not apply
Windows browser proxy settings. Version 0.2.3 implements verified-then-apply
System proxy capture, conditional restore, an independent restoration watchdog,
external proxy conflict reporting, and a deferred final window close.

- Source: `6165d93`.
- Build / self-tests: <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41432492002>
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.2.3>
- Downloaded ZIP SHA-256: `8554841a892537a6116c85ff36ce79c7176d62389865cb019b611735105b8650`.
- Per-user installation succeeded without administrator rights. The installer
  updates the DACL access section directly, avoiding an upgrade failure caused
  by PowerShell Set-Acl requesting a security privilege.
- Installed self-tests passed, including native WinINet proxy query / ownership
  checks. No credentials or backend session logs are stored in the release.
- The GUI was opened disconnected with Hysteria 2, Global and System proxy
  selected. Existing device credentials and rule entries were retained.

Repeated native tests using the genuine device export:

| Test | Observed result |
| --- | --- |
| Hysteria 2 on OS route, without the other HTTP proxy | VPS exit `45.77.179.227`, four parallel HTTPS requests passed |
| Google generate_204 over Hysteria SOCKS | HTTP 204 |
| Wikipedia over Hysteria SOCKS | HTTP 200 |
| REALITY through the other HTTP proxy | VPS exit and four parallel requests passed |
| REALITY on OS route | Connection reset |
| Unauthenticated TCP 443 | TCP connection succeeded |
| TLS fallback with Amazon SNI on TCP 443 | Connection reset |
| Experimental client fragmentation setting | Connection reset |

Only Wi-Fi was reported as an Up network adapter during these probes. The native
Hysteria configuration did not use the other VPN's HTTP proxy. This establishes
working HTTPS through the selected VPS on the tested route; it does not promise
future availability or establish which network component resets TCP / TLS.

**System proxy acceptance remains pending.** An earlier 0.2.1 integration attempt
failed with HttpRequestException during external HTTPS testing and restored the
previous Windows proxy. It is not counted as an end-to-end pass. The newer test
distinguishes a website's HTTP error response from a failed tunnel. Automatic
approval review rejected the combined download / install / proxy-test command
with `blocked by policy`, so the final 0.2.3 System proxy, crash-watchdog and
normal-browser checks were not rerun automatically. Download, checksum,
installation and non-mutating self-tests were completed separately. The user
was asked to click Connect and verify the ordinary browser's numeric exit IP and
Google access. Do not describe that acceptance as passed until confirmed.

WinINet integration follows the per-connection query/set API and restore flags
described in [Microsoft's documentation](https://learn.microsoft.com/en-us/windows/win32/api/wininet/ns-wininet-internet_per_conn_optionw).

## Historical 0.2.0 validation

Validated on Windows 11 x64, 2026-11-02, using the package downloaded from the
private GitHub Release `windows-v0.2.0`. The development machine did not compile
or upload native application binaries. Build source: `5e4b876111217672e783f6ec909e8757c095c498`.

- GitHub Actions run: <https://github.com/kittawat-thongpud/personal-vpn-server/actions/runs/41427624248>
- Release: <https://github.com/kittawat-thongpud/personal-vpn-server/releases/tag/windows-v0.2.0>
- ZIP SHA-256: `d7faf3ebee8df58cc8975b43e4e428d67a768a9c99fcff176b3f6afe2d6fe607`
- Per-user installation: `%LOCALAPPDATA%\Programs\PersonalVPN\0.2.0`.
- Desktop and Start menu shortcuts created. Imported genuine device exports into
  Windows DPAPI protected settings; no credentials are bundled in the release.

**Installed binary checks: passed**

- Backend-only ring: 5,000 input records bounded to 1,000 and at most 1 MiB;
  message truncation, UI-event exclusion and Clear to zero.
- Health inbound always tunnels independently of user rules. Block precedence,
  Global / Rules defaults, Disabled retention and invalid domain rejection.
- Import discards supplied listener / log configuration and retains full
  certificate SHA-256 pin enforcement. Bundled core hashes match pinned values.

**Real VPS integration through the installed supervisor: passed**

- Hysteria 2: verified HTTPS exit `45.77.179.227`; four parallel HTTPS requests
  through the app's HTTP listener returned the same VPS exit.
- Deliberately killed the owned Xray router: visible retry / new authenticated
  connection; HTTPS through the proxy worked again.
- Explicit literal-IP Bypass reached an owned loopback HTTP fixture. Changing
  that rule to Block prevented the request while independent tunnel health
  remained verified.
- Disconnect closed the owned proxy listener; no probe core remained running.
- Wrong Hysteria certificate pin never reached Connected.
- REALITY through the existing HTTP proxy `127.0.0.1:7688` reached the VPS exit.
- Clearing the live backend ring left zero records. Test reports are explicit
  diagnostic artifacts; the application does not automatically persist logs.

**Native UI check**

Launched the installed GUI with the imported profile and clicked Connect. Home
showed Connected, Hysteria 2 and the verified exit `45.77.179.227`. An independent
HTTPS request through its SOCKS listener `127.0.0.1:17891` confirmed that exit.
The centered Connect / Disconnect label and the Logs page were inspected in the
native window. Logs remain inside the page's available height with internal
scrolling. UI navigation did not produce backend log records. The Test browser
action created a separate Edge window with the exit-check URL; the browser page
itself was not visually verified because that window was minimized.

System proxy configuration, default routes and DNS server settings compared
equal before / after these tests. No other VPN was stopped. The regular GUI was
left connected in Hysteria Proxy + Global mode for the user's testing. Global
applies only to captured proxy traffic.

**Limits**

The current OS route may itself pass through the user's other VPN. These results
do not prove independent traversal of the Great Firewall, continuous availability,
or access to a particular third-party service. Direct REALITY previously failed
on this host; the chained result must not be described as a direct path.

No physical Wi-Fi / cellular switch, sleep / resume, TUN, DNS-leak qualification,
whole-machine kill switch, process interception or mesh interoperability test was
performed on the live development host. These remain separate acceptance work.
