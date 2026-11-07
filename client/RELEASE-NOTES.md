Client 0.9.1 fixes Windows LAN / Wi-Fi Direct egress and capture recovery.

Direct sockets now choose the longest matching destination route on connected
Ethernet / Wi-Fi, then route + interface metric. A fixed default-interface bind
no longer sends another link's local destinations to the wrong adapter. Unsafe
Forwarding / WeakHostSend bindings fail visibly instead of recursing into TUN.

An independent guard durably journals the physical GUID / address-family flags
needed for strong-host binding before changing them. It restores only those
fields on stop, mode change or abrupt GUI / native-core death, preserving DNS,
DHCP, MTU, metrics and unrelated settings. Active Internet sharing / routing is
reported as a conflict, rather than silently disabling it. Missing USB interfaces
retain recovery metadata until they return; plaintext runtime configs are removed.
The native core no longer makes unjournaled physical-interface changes.

Capture reset verifies routes, addresses and DNS even when the capture address
has disappeared already. Cleanup steps run independently; failed cleanup retains
recovery data and prevents a fresh capture until recovery succeeds. Windows proxy
restoration is read back before its recovery record is deleted. Metric changes
are checked while connected, alongside address / availability events.

App rules still precede Site rules; Site Bypass does not create global host-route
exclusions. Existing ZeroTier / Tailscale prefixes and interfaces are preserved.
New isolated Windows CI cases cover destination selection, physical flag recovery,
forced owner death and App Block over Site Bypass. A university captive portal
still requires its own sign-in; successful recovery does not authenticate it.

The signed Android APK uses the same 0.9.1 client version; its VPN behavior is
unchanged in this Windows fix. All binaries are built in GitHub Actions.

Previous feature baseline (0.9.0):

Windows App Tunnel shows the executable icon, filename and action in one row,
with Edit and Delete controls at the end. Icons are extracted from local files
on two background workers, cached in a bounded session cache, and loaded only
for realized list rows. Missing executables have a fallback icon. Android App
Tunnel retains its installed-app selector and batch actions.

Site Tunnel shows a domain or network icon, an optional group name (otherwise
the target list), and the action before Edit/Delete. Long summaries ellipsize;
all targets remain available in the editor. A group uses one target type and
one action. Separate domains or IPs with commas or new lines, for example:
www.google.com,www.youtube.com or 2.2.2.*,10.46.136.6,2.2.2.2.
Trailing IPv4 wildcards compile to CIDR (2.2.2.* is 2.2.2.0/24). IPv6 and CIDR
remain supported. Validation rejects malformed members and duplicate canonical
targets across groups before saving the whole edit; at most 500 effective site
targets may be stored. Existing single-target rules need no migration.

Explicit App exceptions retain precedence over Site rules. Windows keeps its
existing Block-first site order; Android keeps insertion order. Group names
are presentation metadata and never affect routing. This release does not
change VPN transport, recovery, adapters, tray behavior or the VPS.

All installers and native packages are built and qualified in GitHub Actions.
The client version is shared by the signed Android APK and Windows x86/x64
Setup and portable packages. P2P development remains paused.
