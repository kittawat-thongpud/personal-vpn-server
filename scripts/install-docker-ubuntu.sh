#!/bin/sh
# Install Docker Engine + Compose on a fresh Ubuntu 24.04 VPS.
# Does not initialize VPN credentials, start VPN services, or change firewall rules.
set -eu
if [ "$(id -u)" -ne 0 ]; then
  echo 'Run: sudo sh scripts/install-docker-ubuntu.sh' >&2
  exit 1
fi
. /etc/os-release
if [ "$ID" != ubuntu ] || [ "$VERSION_ID" != 24.04 ]; then
  echo 'This installer supports Ubuntu 24.04 only.' >&2
  exit 1
fi
if command -v docker >/dev/null 2>&1; then
  docker version
  docker compose version
  echo 'Docker already installed; keeping the existing installation.'
  apt-get update
  apt-get install -y python3-venv
  exit 0
fi
if dpkg-query -W -f='${Status}' docker.io 2>/dev/null | grep -q 'install ok installed'; then
  echo 'Existing distro Docker detected. Configure Compose for it instead of mixing repositories.' >&2
  exit 1
fi
apt-get update
apt-get install -y ca-certificates curl python3-venv
install -m 0755 -d /etc/apt/keyrings
temporary=$(mktemp)
trap 'rm -f "$temporary"' EXIT HUP INT TERM
curl --fail --silent --show-error --location https://download.docker.com/linux/ubuntu/gpg -o "$temporary"
install -m 0644 "$temporary" /etc/apt/keyrings/docker.asc
if [ ! -e /etc/apt/sources.list.d/docker.sources ]; then
  arch=$(dpkg --print-architecture)
  cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: noble
Components: stable
Architectures: $arch
Signed-By: /etc/apt/keyrings/docker.asc
EOF
fi
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
docker version
docker compose version
