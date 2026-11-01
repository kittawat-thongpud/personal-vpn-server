# Publish a release

Repository: `kittawat-thongpud/personal-vpn-server` (**Private**).

Push reviewed source to `main`. The workflow verifies the control plane and real
native Linux TCP/UDP tunnels, builds both container targets and tests their startup
with nonroot users, low ports, read-only config mounts and a writable ACME volume.
Only a successful verification job can publish images or a GitHub Release.

The publish job uses the repository's short-lived `GITHUB_TOKEN` with
`contents:write` and `packages:write`. No long-lived publishing token, VPS password,
SSH private key, VPN state or device credential is needed in Actions secrets.

Images are published privately to:

```text
ghcr.io/kittawat-thongpud/personal-vpn-server/xray:v0.1.0
ghcr.io/kittawat-thongpud/personal-vpn-server/hysteria:v0.1.0
```

Each versioned GitHub Release includes:

- `personal-vpn-v0.1.0.tar.gz`: Python CLI, requirements, deployment docs, installer
  helper and two Compose modes. The default uses locally loaded images; the GHCR
  mode uses exact image digests. Neither mode has a `build` section.
- `images-linux-amd64-v0.1.0.tar.gz`: both prebuilt Docker images for the selected
  x64 VPS, usable without logging into GHCR.
- `SHA256SUMS`: checksums of those two archives.

The release is initially a draft and is published only after both assets and
checksums have uploaded. The tag points at the verified source commit. No `latest`
tag is used and existing releases are not overwritten. Increment `VERSION` (for
example `0.1.1`) before publishing another version. A push of the same version runs
verification but skips publishing an existing release.

## First run

1. Create the private repository and push these files to `main`.
2. Open Actions -> Verify and publish VPN images. If organization/account policy
   requires enabling Actions or packages, the owner must enable it.
3. Wait for both `verify` and `publish` to pass, then inspect the release assets.
4. Follow DEPLOY.md on the VPS. A workflow file in Git is not evidence of a
   successful build; check the actual run before attempting installation.

If publishing fails after creating a draft release, inspect that draft and the
run before retrying. The workflow intentionally does not delete or overwrite an
existing draft/release or move its tag. Fix the cause, then either finish the
existing draft manually or increment `VERSION` and publish a new version.

Private-repository Actions usage and GHCR storage count toward the account plan's
included allowances. Workflows have 20-minute job timeouts; no automatic recurring
build is configured.

## Local checks

```sh
python -m pip install -r requirements-dev.txt
# Linux requires sudo for the control-plane permission checks.
python -m unittest discover -s tests -p 'test_*.py' -v
actionlint .github/workflows/release.yml
```

Container smoke requires Linux Docker plus both images built with the tags in
compose.yaml. `scripts/container_smoke.py` uses its own temporary configuration,
project and volume, then removes only that test project's resources. It does not
start a production deployment or apply host tuning/firewall changes.
