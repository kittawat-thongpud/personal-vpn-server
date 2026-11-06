# Client UI 0.8.0

Android and Windows share a centered mobile layout, dark gradient background,
outlined rounded cards, teal transfer/upload and blue receive/download colors.
Connection state comes from the backend; changing a draft does not invent a
Connected state. Windows keeps native tray tracking and root Proxy/TUN controls.

## Lists and input

Android App Tunnel enumerates apps off the UI thread and retains the process
catalogue and bounded icon cache. Rows use 64 dp minimum height; large fonts may
increase it. Only visible icons are loaded. Search/filter controls collapse on
user scroll, remain while editing and return on upward scroll or Search. The
3 dp list thumb is visible during scrolling. Package IDs are in details or shown
for duplicate names. Back and the current default action remain reachable.

Batch selection persists across search/filter changes. Select shown targets
only the current filtered list. Choose action explicitly opens a sheet; selection
alone does not interrupt the user. Apply preserves unrelated rules, Use default
removes only selected exceptions, and enabling paused rules requires confirmation.
Saved changes require reconnect when a session is already active.

Subpages use Back instead of permanent root navigation on Android. Sheets scroll
inside the available screen/keyboard insets. OS permissions and file pickers stay
native. On short screens or large Android font settings Home may scroll its
compact contents; normal Home fits its viewport. Windows tests retain the
380x590 minimum-layout acceptance with a readable Connect control.

## Traffic

Rates/totals remain backend payload measurements. Up/down share one binary-unit
B/s, KiB/s or MiB/s scale with rounded 1/2/5 ticks, zero baseline and gentle grid.
The time axis covers 60 seconds with 15-second divisions. Each sample remains a
curve endpoint; horizontal control handles avoid negative/overshooting values.
A peak expands the scale immediately, and fifteen quiet samples allow one lower
scale step. New sessions must not show samples from an earlier connection.

## Latency

The foreground UI has a separate in-memory TCP-connect cache, shared by Home
(selected profile) and Profiles (all). Profile/port changes invalidate the key;
credentials never enter the key. Android observes physical-link changes only
while the Activity is started and invalidates obsolete results. Initial/due measurements run at five-minute
intervals; manual refresh deduplicates pending requests, with two workers and a
three-second per-probe deadline. Hiding/minimizing Windows or stopping the
Android Activity cancels UI probes. The VPN recovery/notification worker remains.

Probes bind to a non-VPN Android Network or a gateway-bearing physical Windows
IPv4 interface with IP_UNICAST_IF and a bound source address. They create no
routes or cores. DNS resolution is outside the TCP stopwatch but inside the
probe deadline. Third-party network filters may still constrain connectivity.
A profile may expose only UDP: TCP Unavailable/Timeout is not an Offline/VPN
health judgement. The details show method and last checked time. No latency or
UI interaction events consume the backend log ring or persist to disk.

Android uses the existing hexagon/power logo as a monochrome notification and
Quick Settings icon, with transparent cut-outs for Android's status-bar tint.
The launcher retains its full-color icon. Android's own VPN/key/privacy system
indicators remain controlled by the OS.

## Qualification

Code, signed installers and previews are produced in GitHub Actions. UI and
network checks must pass for both Windows architectures and the Android 14
emulator before publishing one shared stable client release. Redmi/HyperOS
physical-device behavior and Windows Explorer overflow interaction require
actual-device acceptance; emulator/headless tests do not establish those facts.
