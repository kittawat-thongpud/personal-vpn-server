#!/bin/sh
# Optional Linux-host tuning. Default: print only. No firewall rules are changed.
set -eu

mode=${1:---show}
case "$mode" in
  --show|--apply) ;;
  *) printf 'Usage: %s [--show|--apply]\n' "$0" >&2; exit 2 ;;
esac
if [ "$#" -gt 1 ]; then
  printf 'Usage: %s [--show|--apply]\n' "$0" >&2
  exit 2
fi
if [ "$(uname -s)" != Linux ]; then
  echo 'This optional tuning script requires a Linux VPS.' >&2
  exit 1
fi

config=/etc/sysctl.d/90-personal-vpn.conf
emit_config() {
  cat <<'EOF'
# Managed by personal-vpn scripts/host-tuning.sh
# Optional host defaults; does not reserve or guarantee bandwidth.
# These are maximum socket buffer sizes, not per-socket preallocations.
net.core.rmem_max = 8388608
net.core.wmem_max = 8388608
net.core.somaxconn = 4096
# New interfaces use fq; this does not replace an active interface's qdisc.
net.core.default_qdisc = fq
EOF
  if printf ' %s ' "$controllers" | grep -q ' bbr '; then
    printf 'net.ipv4.tcp_congestion_control = bbr\n'
  fi
}

controllers=$(sysctl -n net.ipv4.tcp_available_congestion_control 2>/dev/null || true)
if [ "$mode" = --show ]; then
  printf 'Proposed optional settings for %s:\n\n' "$config"
  emit_config
  cat <<'EOF'

To apply deliberately: sudo sh scripts/host-tuning.sh --apply
BBR is included only when the host kernel exposes it. Apply mode attempts to
load tcp_bbr and sch_fq first. Host TCP BBR does not configure Hysteria QUIC BBR.
For existing Docker network namespaces, recreate containers after applying.
Measure throughput, latency under load, and packet loss before and after.
No setting combines links or guarantees a speedup. Removal of the config file
does not restore live values; record previous values before applying.
EOF
  exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
  echo 'Apply mode needs root: sudo sh scripts/host-tuning.sh --apply' >&2
  exit 1
fi
if [ -e "$config" ] && ! grep -q '^# Managed by personal-vpn scripts/host-tuning.sh$' "$config"; then
  printf 'Refusing to overwrite an unrelated existing file: %s\n' "$config" >&2
  exit 1
fi
if command -v modprobe >/dev/null 2>&1; then
  modprobe tcp_bbr 2>/dev/null || true
  modprobe sch_fq 2>/dev/null || true
fi
controllers=$(sysctl -n net.ipv4.tcp_available_congestion_control 2>/dev/null || true)
if ! printf ' %s ' "$controllers" | grep -q ' bbr '; then
  echo 'TCP BBR unavailable; leaving TCP congestion control unchanged.' >&2
fi

echo 'Current live settings (save these if you want to undo tuning):'
sysctl net.core.rmem_max net.core.wmem_max net.core.somaxconn \
  net.core.default_qdisc net.ipv4.tcp_congestion_control
temporary=$(mktemp)
trap 'rm -f "$temporary"' EXIT HUP INT TERM
emit_config > "$temporary"
install -m 0644 "$temporary" "$config"
sysctl -p "$config"
echo 'Applied optional settings. Recreate existing containers, then measure.'
