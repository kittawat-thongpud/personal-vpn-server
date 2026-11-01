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
mkdir -p "$HOME/vpn-release-v0.1.0"
cd "$HOME/vpn-release-v0.1.0"
read -r -s -p 'GitHub read-only token: ' GH_TOKEN
printf '\n'
export GH_TOKEN
gh release download v0.1.0 --repo kittawat-thongpud/personal-vpn-server \
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
sudo tar -xzf personal-vpn-v0.1.0.tar.gz -C /opt/personal-vpn
cd /opt/personal-vpn
sudo sh scripts/install-docker-ubuntu.sh
```

The helper follows Docker's official Ubuntu APT repository. It installs Docker
Engine, Compose, and Python venv. It leaves existing working Docker installations
in place, does not reboot, and does not change firewall rules.

Load the image archive downloaded above (replace the path if you used a different
account or download directory):

```bash
sudo docker load -i "$HOME/vpn-release-v0.1.0/images-linux-amd64-v0.1.0.tar.gz"
sudo python3 -m venv .venv
sudo .venv/bin/python -m pip install -r requirements.txt
```

## Select the REALITY target, initialize, and start

Replace both `YOUR_TARGET_HOST` and `YOUR_TARGET_SNI` with a selected TLS1.3/H2
target reachable from the VPS. Do not use these placeholders literally.

```bash
sudo install -d -m 0750 -g 10001 runtime
sudo docker compose run --rm xray tls ping YOUR_TARGET_HOST:443
sudo .venv/bin/python vpnctl.py init \
  --endpoint YOUR_VPS_IP \
  --reality-target YOUR_TARGET_HOST:443 \
  --reality-sni YOUR_TARGET_SNI
sudo .venv/bin/python vpnctl.py add-device windows
sudo .venv/bin/python vpnctl.py doctor
sudo docker compose --profile hy2 config --quiet
sudo docker compose run --rm xray run -test -config /etc/vpn/xray.json
sudo docker compose --profile hy2 up -d --no-build
sudo docker compose --profile hy2 ps
```

The release `compose.yaml` has **no build instructions** and uses `pull_policy:
never`, so it uses only the versioned images from `docker load`. Native services
still use the same nonroot UID, read-only filesystem, and isolated Docker bridge.
Secrets are generated on the VPS, not in GitHub Actions.

Allow cloud firewall TCP443 and UDP443, plus SSH22 restricted to your admin IP.
TCP80 is only needed later for ACME HTTP challenges. See README.md for credentials,
client exports, pin enforcement, certificate rotation, tuning, and revocation.

## Updates and rollback

For a new version, download its release assets to a **new** directory, verify
SHA256SUMS, and load the image archive. Back up existing `.state/`, `runtime/`, and
the named ACME volume before changing deployment files. Do not replace these state
directories with files from another server or rerun `init` on an existing deployment.

Replace only the release-managed source/configuration templates with the new
version, run `vpnctl.py render`, then `docker compose --profile hy2 up -d --no-build`.
Keep the previous Compose files and images for rollback. Do not run `down -v`.

## Direct private GHCR pull (optional)

`compose.registry.yaml` uses exact image SHA256 digests and supports amd64/arm64.
Private container pulls require authentication to `ghcr.io` with a token allowed
to read those packages (GitHub documents a classic PAT with `read:packages`).
Use `docker login ghcr.io --password-stdin` with that token, then consistently use
`docker compose -f compose.registry.yaml --profile hy2 ...` for this deployment.
The default release-archive path above only needs read access to this repository's
release assets and avoids storing registry credentials on the VPS.

References: [GitHub private release download](https://cli.github.com/manual/gh_release_download),
[fine-grained repository permissions](https://docs.github.com/en/rest/releases/assets),
[GHCR authentication](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry),
[Docker Ubuntu installation](https://docs.docker.com/engine/install/ubuntu/).
