# Personal VPN client releases

`VERSION` is the source of truth for the client feature version. Windows assembly,
installer and ZIP versions and Android versionName/versionCode are derived from
it. Increment this file for a new published client feature/fix baseline. Server
container versions remain independent.

The **Client release** workflow builds/tests Windows x86, Windows x64 and Android
from one commit, then publishes `client-v<version>` with every installer, portable
ZIP, platform guide, Android signature report, SHA256SUMS.txt and CLIENT-RELEASE.json.
No partial platform release is published if a required platform fails. Signing
keeps the existing Android identity so a normal APK update preserves settings.
Builds and installer uploads occur in Actions. The optional manual `publish=false`
run qualifies code without creating a release or modifying snapshot branches.

## Source branches

- `main`: ongoing development.
- `base/client-v<version>`: the qualified baseline for that release; never moved.
- `release/client-v<version>`: initial source matches the installers; branch here
  for maintenance, increment VERSION and run the same all-platform pipeline.
- `legacy/windows-v0.6.3`, `legacy/android-v0.1.0`: previous independent baselines.
- `legacy/client-v<version>`: an earlier unified baseline, preserved automatically
  when a later client is published.

Tags and existing branches are never force-updated. A version's Windows and
Android installers are kept together in its release rather than being renamed
from binaries with unrelated internal versions. Source and each asset's checksum
are recorded in CLIENT-RELEASE.json. Platform limitations remain documented in
the Windows and Android guides; a common number does not imply identical APIs.
