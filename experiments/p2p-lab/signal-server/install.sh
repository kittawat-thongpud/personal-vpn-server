#!/usr/bin/env bash
set -euo pipefail
test "$(id -u)" = 0 || { echo 'Run this installer in the VPS administrator console.'; exit 1; }
cd "$(dirname "$0")"
test "$(pwd -P)" != /opt/personal-vpn || { echo 'Use /opt/personal-vpn-p2p-lab, separate from the Internet VPN.'; exit 1; }
test -f p2p-signal-image.tar.gz
sha256sum -c SHA256SUMS.txt
docker load -i p2p-signal-image.tar.gz
install -d -m 750 -o 10001 -g 10001 signal-state
if ! test -f signal-state/server.key; then
  docker compose -f compose.signal.yaml run --rm --no-deps signal init --endpoint https://45.77.179.227:9443
fi
docker compose -f compose.signal.yaml up -d --no-build --pull never
docker compose -f compose.signal.yaml ps
echo 'Open TCP 9443 in the Vultr firewall if a firewall group is attached.'
echo 'Paste the private owner enrollment into Windows P2P Lab; do not send it in chat.'
echo 'If the 10-minute invitation expired, run: docker compose -f compose.signal.yaml exec signal python /app/server.py owner-invite'
