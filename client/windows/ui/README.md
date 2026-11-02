# Windows client UI preview

`preview.html` is an interactive design fragment, inspired by the compact VPN
layout supplied by the user. It is a local simulation, not a Windows VPN binary.
No real device profile, password, server key or network measurement is included.

The Codex preview provides two layout choices: Compact and Workspace. The same
fragment can be opened directly in a modern browser for the Compact layout;
icons, layout switching and design controls are enhanced by the Codex renderer.
Connect uses a centered text label. Back/add/close controls use visible native
text glyphs. Country flags are bundled assets and work without network requests.

- Click the Home server card to add, edit, select or delete sample profiles.
  Choose a country to update the flag. Metadata is independent of app releases;
  real authentication material is deliberately not accepted by this mockup.
- Choose Capture mode independently from Traffic mode. Protocol/mesh/recovery
  controls and protocol limitations have their own VPN page.
- Edit separate Apps, Websites and IP/Networks lists. Each category has its own
  Tunnel, Bypass, Block or Disabled mode. The category order is visible.
- Inspect a draft route decision and its matching rule. All addresses are examples.
- Configure the proposed mesh behavior; compatibility is explicitly unverified.
- Connect, change settings, apply/reconnect, and simulate network or core failure.
- Review only simulated VPN backend events, clear them, and display their JSON for copying. This works
  inside the conversation sandbox without a browser download or clipboard API.

Starting or interacting with this preview does not start a core, register a
service, set system proxy, alter routes/DNS/firewall, connect to a VPS, or stop
the user's existing VPN. There are no application fetch/XHR/WebSocket calls.
The UI's URL constructors only parse and normalize domain/IP input locally.

The event ring keeps the newest 1,000 backend entries and at most 1 MiB of encoded
event data. UI clicks/navigation/metadata editing do not enter it. It is held in
RAM only, resets on reload and has Clear; events are not persisted in widget state.
The pane scrolls vertically at a bounded height. Metadata/rule choices may be
remembered through Codex's private UI state; direct-file sessions have no automatic
disk persistence. Country flags come from flag-icons 7.2.3; the MIT license and
source checksums are in `flags/` and embedded with the image data. Other/Unspecified
is available for servers outside the sample country list; the country icon never
limits which server address can be entered.

Prototype rule
matching is for interaction review; it is not the production policy compiler or
an OS-enforced kill switch. PreserveMesh uses documented example prefixes, not
adapters discovered on the user's machine. The installed client must implement
and pass the independent acceptance criteria in `../DESIGN.md`.
