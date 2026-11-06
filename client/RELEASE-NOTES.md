Client 0.9.0 makes tunnel exceptions compact and adds named multi-target Site groups on Windows and Android.

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
