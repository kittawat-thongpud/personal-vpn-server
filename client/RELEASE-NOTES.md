Client 0.8.0 refreshes the Android and Windows interface under one client version.

Home keeps the server and VPN-protocol controls in a centered mobile column.
Gradient surfaces, outlined cards and a dimensional Connect button use actual
connection state. Traffic is a subdued curved area graph with horizontal and
vertical grid lines, a 60-second time axis and one rounded upload/download scale.
Spikes expand the scale immediately; quieter windows settle gradually. Curves
pass through measured points without introducing negative values or overshoots.

Android App Tunnel uses compact installed-app rows, cached icons and a search/
filter header that collapses while scrolling. Package IDs move to app details
except for duplicate names. Selection survives searches and filters. A contextual
bar appears only after selection; Choose action opens the batch-action sheet.
Use default removes selected exceptions. Rules-off still requires explicit
confirmation before enabling rules. Explicit App rules keep priority over Site.

Settings have consistent icons. Protocol/default-action choices, import, site
editing and app details use bottom sheets; long profile forms scroll inside the
sheet with keyboard insets. Android notification and Quick Settings icons use
the monochrome Personal VPN hexagon/power logo, replacing the warning shield.

Server cards share a foreground-only latency cache: initial/due checks every five
minutes, manual refresh, two concurrent workers and a three-second deadline.
Home shows the selected server; profiles show all. The method is TCP connect on
a physical interface, not ICMP ping, authenticated VPN health or QUIC RTT. A TCP
failure is Unavailable/Timeout and does not mean Hysteria 2 is offline. TCP-only
probes do not start extra cores, change routes or affect the recovery loop.
Windows probes currently require an IPv4 physical path; Android supports the
addresses resolved on its selected physical network. Checks stop in background/
tray, have no UI event logging and do not store credentials or latency on disk.

Windows keeps the stable install directory, encrypted settings, Administrator
requirement, native tray-menu tracking and taskbar behavior from 0.7.3. The
release includes the signed Android APK and Windows x86/x64 Setup/portable
packages, built and qualified in GitHub Actions. No VPS changes are required.
P2P development remains paused.
