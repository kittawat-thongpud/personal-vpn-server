# Personal VPN client releases

`VERSION` is the source of truth for the client feature version. Windows assembly,
installer and ZIP versions and Android versionName/versionCode are derived from
it. Increment this file for a new published client feature/fix baseline. Server
container versions remain independent.

The **Client release** workflow builds/tests Windows x86, Windows x64 and Android
from one commit, then publishes `client-v<version>` with every installer, portable
ZIP, platform guide, Android signature report, SHA256SUMS.txt and CLIENT-RELEASE.json.
Published client versions are normal stable releases and explicitly marked Latest.
Pre-release is reserved for an explicitly requested development/test build.
No partial platform release is published if a required platform fails. Signing
keeps the existing Android identity so a normal APK update preserves settings.
Builds and installer uploads occur in Actions. The optional manual `publish=false`
run qualifies code without publishing a stable release or modifying snapshot branches.
Private QA drafts can hold checked installers and synthetic UI diagnostics for
review, and are removed after publication.
For an already published version, changes limited to release automation/docs
validate without rebuilding or replacing its installers. App/native/package
changes still require a version increment.

The release list keeps the current Client as Latest, the previous stable Client
as a rollback, the 0.7.2 installer-upgrade fixture, server versions needed by the
VPS, and the Windows 0.6.0 CI dependency bundle. Obsolete independent
Windows/Android installer releases can be removed after checking references;
their source tags and baseline/legacy branches remain available.

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

If the Actions token cannot create a source snapshot, publication stops. After
qualification, create the requested refs with the existing authorized local
GitHub CLI/git login and retry only the failed publisher. Do not upload a broad
personal token to CI or force-update an existing snapshot. This migration needed
that local ref-creation step for the initial 0.7.0 snapshots; the installed
binaries were still built, tested and uploaded entirely in Actions.

## UI and latency

See [UI design](UI-DESIGN.md) and [qualification evidence](VALIDATION.md).
Home has the protocol selector, dimensional connection control and a shared-scale
60-second traffic graph. Server cards share a foreground-only five-minute TCP
latency cache with manual refresh; its details distinguish TCP reachability from
VPN health. Android uses compact multi-select App Tunnel lists, contextual action
sheets and the Personal VPN logo in notifications/Quick Settings.
