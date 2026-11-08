Android follows the shared Client release version, alongside Windows x86/x64.
Use the [shared release notes](../RELEASE-NOTES.md) for current changes and
[Client validation](../VALIDATION.md) for the qualified source, Actions results
and remaining physical-device acceptance.

Client 0.9.4 adds focused background guidance with numbered steps and one-time
manual tips remembered automatically, without confirmation. It retains Android
pending-connection Cancel, readable pending colors, task-removal cleanup and
left-aligned protocol choices. The signed APK supports Android 10+ on arm64-v8a and
x86_64, retains the existing signing certificate and can update an existing
installation. Builds and qualification run in GitHub Actions.

Framework tests pass on Android 14 and 17; physical Samsung/Redmi background and
China-network behavior remain separate acceptance checks. Android permits one
VPN per user/profile, so separate ZeroTier/Tailscale VPN apps cannot run alongside
Personal VPN in that same profile.
