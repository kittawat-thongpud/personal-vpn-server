# Windows client UI preview

`preview.html` is an interactive design fragment, inspired by the compact VPN
layout supplied by the user. It is a local simulation, not a Windows VPN binary.
No profile, password, server key or real network measurement is included.

The Codex preview provides two layout choices: Compact and Workspace. The same
fragment can be opened directly in a modern browser for the Compact layout;
icons, layout switching and design controls are enhanced by the Codex renderer.
The primary connect control has a text fallback when icons are unavailable.

- Choose Capture mode independently from Traffic mode and VPN protocol.
- Read protocol limitations and the scope of Proxy versus TUN.
- Edit sample domain, executable and IP/CIDR rules, including Direct and Block.
- Inspect a draft route decision and its matching rule. All addresses are examples.
- Configure the proposed mesh behavior; compatibility is explicitly unverified.
- Connect, change settings, apply/reconnect, and simulate network or core failure.
- Review local simulated events and display their JSON for copying. This works
  inside the conversation sandbox without a browser download or clipboard API.

Starting or interacting with this preview does not start a core, register a
service, set system proxy, alter routes/DNS/firewall, connect to a VPS, or stop
the user's existing VPN. There are no application fetch/XHR/WebSocket calls.
The UI's URL constructors only parse and normalize domain/IP input locally.

The in-memory events are capped at 100 and reset on reload. Prototype rule
matching is for interaction review; it is not the production policy compiler or
an OS-enforced kill switch. PreserveMesh uses documented example prefixes, not
adapters discovered on the user's machine. The installed client must implement
and pass the independent acceptance criteria in `../DESIGN.md`.
