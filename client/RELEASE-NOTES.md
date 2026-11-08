Client 0.9.3 makes Android pending connections cancellable and stops VPN when
the app task is removed from Recent Apps.

Protocol options align names/descriptions at the left. A fresh Launcher entry
can show background reliability guidance with current Android checks, separate
Samsung/Xiaomi manual instructions and Settings buttons. It distinguishes
Unavailable from safe status and reads the previous own process-exit reason,
including Android 17's documented memory-limit marker. The sheet waits while
connection work is busy so Cancel stays accessible. Recents/notification/widget
entries do not request startup guidance. No device settings are changed silently.

Connecting, Verifying and recovery now show an enabled Cancel button, even with
edited settings. Pending colors use a blue surface and readable light text;
Stopping remains readable during teardown. Cancellation stops pending work,
releases core/capture and removes the foreground notification/service. Command
generations prevent an older Stop from interfering with a newer Connect.

Home and switching to other apps retain the VPN. Removing the Personal VPN task
stops it through the same cleanup path. Android's managed Always-on setting
retains its existing system-policy behavior. Android 14 regression qualification
uses real stalled HTTPS, both protocols, dirty settings, rapid command changes,
task removal and independent physical-network HTTPS after cleanup.

The shared release includes the 0.9.2 shared-UID fix and the existing Windows
LAN/Wi-Fi recovery changes. Windows runtime, VPS and paused P2P are unchanged.
All platform installers are built and qualified in GitHub Actions. Android
framework qualification is configured for both API 34 and API 37; physical
Samsung/Redmi acceptance remains separate.

Previous fix baseline (0.9.2): Android App Tunnel shared-UID connections.

With App rules enabled, a UID associated with several packages previously became
an unresolved owner and was rejected even with Tunnel by default. Known shared
UIDs now keep all package names for normal App matching. Conflicting exceptions
on the same UID use Block > Tunnel > Bypass, independent of edit order. App
exceptions still precede Site rules. Android identifies these sockets by UID;
packages sharing it cannot be routed independently.

Genuinely missing owners remain rejected while App rules are enabled. Bounded
backend notices distinguish shared ownership from a failed owner lookup, with
at most one notice of each kind per minute. Existing session log limits remain.
Android 14 regression qualification uses two actual shared-UID companion apps,
both VPN protocols and default Tunnel with unrelated App/private-IP Bypass.

Windows includes the existing 0.9.1 LAN/Wi-Fi and recovery fixes below; its VPN
runtime is unchanged in 0.9.2. The shared release contains the signed Android APK
and Windows x86/x64 installers, built and qualified in GitHub Actions. The VPS
and P2P sector are unchanged. YouTube login/data loading on the physical Redmi
still needs acceptance with the user's saved rules after updating the APK.

Previous fix baseline (0.9.1): Windows LAN / Wi-Fi Direct egress and capture recovery.

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

The 0.9.1 Windows fix did not change Android VPN behavior. All binaries are built
in GitHub Actions.

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
