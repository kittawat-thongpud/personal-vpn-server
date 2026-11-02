# Install a private GitHub Release on the VPS

This path downloads everything from GitHub to the VPS. No upload from the client
in China and no image build on the VPS are needed.

The default release image archive supports **Linux x64/amd64** (the selected Vultr
plan). Private GHCR images also support arm64, through `compose.registry.yaml`.

## Download the private release

On Ubuntu 24.04, install the GitHub CLI from Ubuntu's package repository:

```sh
sudo apt-get update
sudo apt-get install -y gh
```

For this private repository, use a fine-grained GitHub token restricted to
`kittawat-thongpud/personal-vpn-server` with **Contents: Read-only**. Set a short
expiry. The token is only needed for downloading release assets; the image archive
does not require a GHCR token. Enter it at the password-style prompt below; do not
put the token literal in a command or send it in chat.

Run these commands in **Bash on the VPS**:

```bash
mkdir -p "$HOME/vpn-release-v0.1.1"
cd "$HOME/vpn-release-v0.1.1"
read -r -s -p 'GitHub read-only token: ' GH_TOKEN
printf '\n'
export GH_TOKEN
gh release download v0.1.1 --repo kittawat-thongpud/personal-vpn-server \
  --pattern 'personal-vpn-*.tar.gz' --pattern 'images-linux-amd64-*.tar.gz' --pattern SHA256SUMS
unset GH_TOKEN
sha256sum -c SHA256SUMS
```

The first release must have finished publishing in Actions before download works.
Failed checksum or missing assets: stop and inspect the failed download rather than
loading images. `read -s` prevents terminal echo; the token value is not stored in
shell history. `unset` clears it from the current shell after download.

## Install Docker and load the prebuilt images

On the **first installation**, use a fresh deployment directory:

```bash
sudo install -d -m 0755 /opt/personal-vpn
sudo tar -xzf personal-vpn-v0.1.1.tar.gz -C /opt/personal-vpn
cd /opt/personal-vpn
sudo sh scripts/install-docker-ubuntu.sh
```

The helper follows Docker's official Ubuntu APT repository. It installs Docker
Engine, Compose, and Python venv. It leaves existing working Docker installations
in place, does not reboot, and does not change firewall rules.

Load the image archive downloaded above (replace the path if you used a different
account or download directory):

```bash
sudo docker load -i "$HOME/vpn-release-v0.1.1/images-linux-amd64-v0.1.1.tar.gz"
sudo python3 -m venv .venv
sudo .venv/bin/python -m pip install -r requirements.txt
```

## Select the REALITY target, initialize, and start

Replace both `YOUR_TARGET_HOST` and `YOUR_TARGET_SNI` with a selected TLS1.3/H2
target reachable from the VPS. Do not use these placeholders literally.

```bash
sudo install -d -m 0750 -g 10001 runtime
sudo docker compose run --rm --no-deps --interactive=false xray tls ping YOUR_TARGET_HOST:443
sudo .venv/bin/python vpnctl.py init \
  --endpoint YOUR_VPS_IP \
  --reality-target YOUR_TARGET_HOST:443 \
  --reality-sni YOUR_TARGET_SNI
sudo .venv/bin/python vpnctl.py add-device windows
sudo .venv/bin/python vpnctl.py start
sudo docker compose --profile hy2 ps
```

The release `compose.yaml` has **no build instructions** and uses `pull_policy:
never`, so it uses only the versioned images from `docker load`. Native services
still use the same nonroot UID, read-only filesystem, and isolated Docker bridge.
Secrets are generated on the VPS, not in GitHub Actions.

`start` checks the state, required runtime files and container GID permissions,
normalizes Compose and checks both config bind mounts, and runs Xray's config test
**inside its image** before starting both services. It uses explicit absolute
project/Compose paths. It also verifies the mounts on the resulting containers.
You can run it from another directory using absolute paths:

```bash
sudo /opt/personal-vpn/.venv/bin/python /opt/personal-vpn/vpnctl.py check-deployment
sudo /opt/personal-vpn/.venv/bin/python /opt/personal-vpn/vpnctl.py start
```

`check-deployment` leaves the VPN services stopped if they were stopped; its Xray
validation uses a temporary container without publishing ports. `start` recreates
both services to apply the current config, interrupting existing sessions. Neither
command builds an image. Local running status does not establish public tunnel
reachability or REALITY target compatibility.

## Missing config or mount errors

The image contains the core binary. The host deployment must contain:

```text
/opt/personal-vpn/.state/server.json  # authoritative server/device credentials
/opt/personal-vpn/runtime/xray.json
/opt/personal-vpn/runtime/hysteria.json
/opt/personal-vpn/runtime/tls.crt     # pinned mode
/opt/personal-vpn/runtime/tls.key     # pinned mode
```

Compose mounts the **directory** `./runtime` read-only at `/etc/vpn` in both
containers. Paths resolve from the selected Compose project. Do not copy the two
Compose files to another directory while leaving the state/runtime behind, and
do not replace this bind mount with an empty named volume. Docker is configured
to reject a missing host directory rather than silently create one.

If `.state/server.json` exists but generated JSON files are missing, regenerate
them in the same deployment; this preserves device credentials and the TLS pin:

```bash
sudo /opt/personal-vpn/.venv/bin/python /opt/personal-vpn/vpnctl.py render
sudo /opt/personal-vpn/.venv/bin/python /opt/personal-vpn/vpnctl.py start
```

Missing TLS key/certificate files require restoration from backup. Do not run
`rotate-certificate` as a file-path fix: rotation changes the pin on every client.
If the state file is absent, locate the original deployment/backup before running
`init`; initialization is for a genuinely new server. Do not share state, runtime
or exported client profiles when reporting errors. `doctor` and `check-deployment`
report file paths and validation status without printing credential contents.

For manual `docker compose run` commands in pasted Bash scripts, use
`--interactive=false`. Compose's default stdin attachment can otherwise consume
the remaining script input, leaving subsequent startup commands unexecuted.

Allow cloud firewall TCP443 and UDP443, plus SSH22 restricted to your admin IP.
TCP80 is only needed later for ACME HTTP challenges. See README.md for credentials,
client exports, pin enforcement, certificate rotation, tuning, and revocation.

## Updates and rollback

For a new version, download its release assets to a **new** directory, verify
SHA256SUMS, and load the image archive. Back up existing `.state/`, `runtime/`, and
the named ACME volume before changing deployment files. Do not replace these state
directories with files from another server or rerun `init` on an existing deployment.

Replace only the release-managed source/configuration templates with the new
version, run `vpnctl.py render`, then `vpnctl.py start` with the deployment's venv.
Keep the previous Compose files and images for rollback. Do not run `down -v`.

## Direct private GHCR pull (optional)

`compose.registry.yaml` uses exact image SHA256 digests and supports amd64/arm64.
Private container pulls require authentication to `ghcr.io` with a token allowed
to read those packages (GitHub documents a classic PAT with `read:packages`).
Use `docker login ghcr.io --password-stdin` with that token, then consistently use
`vpnctl.py start --registry` or
`docker compose -f compose.registry.yaml --profile hy2 ...` for this deployment.
The default release-archive path above only needs read access to this repository's
release assets and avoids storing registry credentials on the VPS.

References: [GitHub private release download](https://cli.github.com/manual/gh_release_download),
[fine-grained repository permissions](https://docs.github.com/en/rest/releases/assets),
[GHCR authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry),
[Docker Ubuntu installation](https://docs.docker.com/engine/install/ubuntu/).
