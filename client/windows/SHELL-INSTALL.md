# Windows installation and shell behavior

## Installation identity

The Windows client has one product ID and a stable `Program Files\PersonalVPN`
directory. The version belongs to the package metadata, not the installation
path. An ordinary upgrade or same-version reinstall reuses that directory.
On 64-bit Windows both x86 and x64 installers use the native Program Files
root and the same uninstall registration; the x86 application runs under WOW64.

The installer discovers the former architecture-specific registrations and
recognized version directories. It retires those packages with their own
uninstallers rather than recursively deleting directories. Unowned files are
retained. User profiles and rules stay in current-user DPAPI storage outside
the installation directory.

A private maintenance copy asks clients with an exit event to shut down
gracefully. For an older idle/Proxy client it releases the idle adapter service
before Windows Restart Manager closes the GUI. An older active TUN client must
first use its normal Disconnect & exit command; the new exit event supports
automatic graceful shutdown on subsequent upgrades. After that shutdown,
the first installed file's `BeforeInstall` callback checks abandoned owned
leases and stops the adapter service before any application file is replaced.
`ssInstall` is too early: Inno invokes it before performing Restart Manager
shutdown. If cleanup cannot finish, installation fails visibly instead of
replacing a mapped application or force-killing other VPNs. A Setup mutex blocks
a new GUI from racing the update. The updated service is rebound to the stable
executable after legacy uninstallers have finished.

Sources: [Inno previous-directory policy](https://jrsoftware.org/ishelp/topic_setup_usepreviousappdir.htm),
[Restart Manager integration](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm),
[event ordering](https://jrsoftware.org/ishelp/topic_scriptevents.htm), and
[Inno 6 installation source](https://github.com/jrsoftware/issrc/blob/is-6_5_4/Projects/Src/Setup.MainForm.pas).

## Icons and taskbar

Desktop and Start-menu shortcuts use the installed standalone, multi-resolution
`Assets\personal-vpn.ico`. They share `PersonalVPN.Client` with the running
application. An existing per-user shortcut that shadows the common shortcut is
repaired only when it points to a recognized Personal VPN installation. Shell
updates target these shortcut entries rather than clearing the system icon
cache or restarting Explorer.

Opening/restoring the window shows its taskbar button and Alt+Tab entry.
Minimize retains the taskbar button. X hides the window and removes that button
while the tray icon and VPN session remain. Tray Open or the existing shortcut
restores the same window; Disconnect & exit performs normal backend cleanup.

## Tray context menu

NotifyIcon supplies the shell icon and a single right-button-up callback. Its
`ContextMenuStrip` property is deliberately unset. The ToolStrip item tree is
only an action model; the displayed popup is a real Windows HMENU.

The popup has a top-level owner, calls `SetForegroundWindow`, tracks the menu
with `TrackPopupMenuEx`, and posts `WM_NULL` afterwards. Windows handles pointer
tracking, submenus, Escape, outside clicks and command selection. The selected
command is dispatched once. There is no MouseLeave timer, focus-loss override,
global mouse hook or Explorer panel manipulation. Menu styling follows Windows.

This follows the [Microsoft TrackPopupMenu guidance](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackpopupmenu)
and the [PowerToys tray implementation](https://github.com/microsoft/PowerToys/blob/main/src/runner/tray_icon.cpp).
Explorer owns the notification overflow panel. Its visibility is a separate
desktop acceptance case, not something the app forces through undocumented
Shell focus workarounds.

## Acceptance evidence

The isolated Windows workflow checks the native menu tree, command identities,
enabled states, checkmarks, taskbar lifecycle, driver installation and existing
Proxy/TUN regression suites. Real mouse-input tests explicitly report an
unavailable desktop instead of claiming success. Installer integration starts
the published 0.7.2 client, migrates it, starts the new client and reinstalls it.
It checks encrypted settings, common and shadowing user shortcuts, Windows
large/small icon extraction, uninstall identity and retention of unowned files.

On a real Windows 11 desktop, verify these separately:

1. Install with a working alternate connection available; confirm one stable
   install directory, one Apps entry, the desktop icon and retained profiles.
2. Open from the shortcut, minimize, restore, close with X, then restore from
   tray. Check taskbar and Alt+Tab visibility at each stage.
3. Open the notification overflow panel and right-click Personal VPN once.
   Move the pointer away without clicking: the menu stays open.
4. Click blank space inside the overflow panel: only the menu should close.
   Reopen once and click outside both: normal Shell dismissal should occur.
5. Repeat after Escape, a submenu selection and an ordinary menu command.
   Confirm no second right-click is needed and no action fires twice.

Record the actual desktop result and its Windows version. Passing API/state
checks on an Actions runner alone does not establish Explorer overflow behavior.
