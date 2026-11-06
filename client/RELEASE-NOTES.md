Client 0.7.3 fixes Windows installation, shortcut icons and taskbar/tray interaction.

Windows Setup now uses one stable Program Files\PersonalVPN directory and one
uninstall identity for x86/x64. Updates and reinstalls replace the existing
application. Registered legacy and version-named packages are retired through
their own uninstall logs; unowned files are retained. Profiles and rules remain
in current-user DPAPI storage. Restart Manager closes an existing GUI before
replacement, then maintenance recovers only abandoned Personal VPN leases and
stops its adapter service. No installer force-kills another VPN.

Desktop and Start-menu shortcuts point to a standalone multi-size ICO at a
stable path and share the application's AppUserModelID. Existing shadowing
per-user shortcuts are repaired and their shell cache entries refreshed.

An open app appears in the taskbar and Alt+Tab. Minimize retains its taskbar
button; X hides the window to tray. Left-click/Open restores it without starting
a second instance. Disconnect & exit still restores owned network settings.

Right-click uses a native Windows HMENU with foreground ownership, standard
menu tracking and the documented WM_NULL handoff after dismissal. One request
opens the menu; pointer movement alone does not dismiss it. Outside click,
Escape and a selected action dismiss normally. Menus follow Windows styling;
Explorer controls the notification overflow panel. No focus-loss/MouseLeave
cancellation overrides or custom global input hooks are used.

Qualification requires real native popup input tests, visible/hidden taskbar
lifecycle checks, migration from the published 0.7.2 installer, an in-place
reinstall while an idle GUI is running, byte-identical encrypted settings,
shortcut/icon extraction and the full existing Proxy/TUN/driver/cleanup suite.

The shared release includes Android APK and Windows x86/x64 setup/portable
packages built in GitHub Actions. Android networking remains the qualified
0.7.2 recovery implementation and is requalified under the shared version.
VPS configuration and protocol credentials do not change. P2P remains paused.
