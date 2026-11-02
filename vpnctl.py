#!/usr/bin/env python3
"""Offline control plane for a personal Xray + Hysteria server.

No daemon, subscription endpoint, Docker socket, or remote administration API.
Generated credentials are never printed to the terminal.
"""
from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import sys
import tempfile
from urllib.parse import quote, urlencode
import uuid

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, x25519
from cryptography.x509.oid import NameOID

ROOT = Path(__file__).resolve().parent
RUNTIME_GID = 10001
# Explicit CIDRs avoid large geo databases and block host/LAN/cloud metadata.
# IPv4-mapped addresses are normalized by the cores and covered by IPv4 rules.
# Blocking ::ffff:0:0/96 would reject all IPv4 in Go's IPNet matching.
DENY_CIDRS = [
    "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
    "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
    "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
    "224.0.0.0/4", "240.0.0.0/4", "::/128", "::1/128",
    "64:ff9b::/96", "64:ff9b:1::/48", "100::/64", "2001::/32",
    "2001:db8::/32", "2002::/16", "fc00::/7", "fe80::/10", "ff00::/8",
]


def b64url(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


def host(value: str) -> str:
    """Accept a bare IP or a DNS name, never URLs, paths, or port suffixes."""
    value = value.strip()
    try:
        return str(ipaddress.ip_address(value))
    except ValueError:
        pass
    if len(value) > 253 or not value or value.endswith("."):
        raise ValueError("Use a bare IP address or DNS hostname.")
    value = value.encode("idna").decode("ascii").lower()
    if not all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", s)
               for s in value.split(".")):
        raise ValueError("Invalid hostname; do not include a scheme, path, or port.")
    return value


def authority(address: str, port: int) -> str:
    return f"[{address}]:{port}" if ":" in address else f"{address}:{port}"


def target(value: str) -> str:
    match = re.fullmatch(r"\[([^]]+)\]:(\d+)|([^:]+):(\d+)", value)
    if not match:
        raise ValueError("REALITY target must be hostname:port or [IPv6]:port.")
    address, port = (match[1], match[2]) if match[1] else (match[3], match[4])
    return authority(host(address), valid_port(port))


def valid_port(value: str | int) -> int:
    number = int(value)
    if not 1 <= number <= 65535:
        raise ValueError("Port must be in 1..65535.")
    return number


def device_name(value: str) -> str:
    if not re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", value):
        raise ValueError("Device name: 1..32 lowercase letters, numbers, _ or -; start with a letter.")
    return value


def directory(path: Path, runtime: bool = False) -> None:
    path.mkdir(parents=True, exist_ok=True)
    if os.name == "posix":
        if runtime:
            os.chown(path, -1, RUNTIME_GID)
        path.chmod(0o750 if runtime else 0o700)


def write_file(path: Path, value: str | bytes, runtime: bool = False) -> None:
    directory(path.parent, runtime)
    data = value.encode("utf-8") if isinstance(value, str) else value
    fd, temporary = tempfile.mkstemp(prefix=".vpn-", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        if os.name == "posix":
            if runtime:
                os.chown(temporary, -1, RUNTIME_GID)
            os.chmod(temporary, 0o640 if runtime else 0o600)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def write_json(path: Path, value: dict, runtime: bool = False) -> None:
    write_file(path, json.dumps(value, indent=2, ensure_ascii=False) + "\n", runtime)


@contextmanager
def locked(root: Path):
    root.mkdir(parents=True, exist_ok=True)
    lock = root / ".vpnctl.lock"
    try:
        lock.mkdir()
    except FileExistsError:
        raise ValueError("Another vpnctl command is running. If it crashed, remove .vpnctl.lock after checking.")
    try:
        yield
    finally:
        lock.rmdir()


def load_state(root: Path) -> dict:
    path = root / ".state/server.json"
    if not path.exists():
        raise ValueError(f"Missing {path}. Run init in this deployment, or use --root to select the existing deployment. Do not initialize over another server's state.")
    state = json.loads(path.read_text(encoding="utf-8"))
    if state.get("version") != 1:
        raise ValueError("Unsupported state version.")
    return state


def validate_settings(s: dict) -> None:
    s["endpoint"] = host(s["endpoint"])
    s["reality_target"] = target(s["reality_target"])
    s["reality_sni"] = host(s["reality_sni"])
    for key in ("reality_port", "hy2_port"):
        s[key] = valid_port(s[key])
    s["bind"] = str(ipaddress.ip_address(s["bind"]))
    if s["hy2_domain"]:
        s["hy2_domain"] = host(s["hy2_domain"])
        try:
            ipaddress.ip_address(s["hy2_domain"])
        except ValueError:
            pass
        else:
            raise ValueError("hy2-domain must be a DNS hostname, not an IP.")
    if s["hy2_mode"] == "acme":
        if not s["hy2_domain"] or not re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", s["acme_email"]):
            raise ValueError("ACME requires --hy2-domain and a valid --acme-email.")
        if s["reality_port"] == 80:
            raise ValueError("ACME HTTP challenge needs TCP80; REALITY cannot use TCP80.")


def tls_name(s: dict) -> str:
    return s["hy2_domain"] or s["endpoint"]


def generate_certificate(root: Path, s: dict) -> None:
    name = tls_name(s)
    key = ec.generate_private_key(ec.SECP256R1())
    subject = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, name[:64])])
    try:
        san = x509.IPAddress(ipaddress.ip_address(name))
    except ValueError:
        san = x509.DNSName(name)
    now = datetime.now(timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(subject).issuer_name(subject)
            .public_key(key.public_key()).serial_number(x509.random_serial_number())
            .not_valid_before(now - timedelta(minutes=5))
            .not_valid_after(now + timedelta(days=397))
            .add_extension(x509.SubjectAlternativeName([san]), critical=False)
            .sign(key, hashes.SHA256()))
    write_file(root / "runtime/tls.key", key.private_bytes(
        serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
        serialization.NoEncryption()), True)
    write_file(root / "runtime/tls.crt", cert.public_bytes(serialization.Encoding.PEM), True)


def fingerprint(root: Path) -> str:
    cert = x509.load_pem_x509_certificate((root / "runtime/tls.crt").read_bytes())
    return cert.fingerprint(hashes.SHA256()).hex()


def render(root: Path, state: dict) -> None:
    s = state["settings"]
    devices = {n: d for n, d in state["devices"].items() if d["active"]}
    xray = {
        "log": {"loglevel": "warning", "access": "none"},
        "inbounds": [{
            "tag": "reality-in", "listen": s["bind"], "port": s["reality_port"],
            "protocol": "vless",
            "settings": {"decryption": "none", "users": [
                {"id": d["uuid"], "email": n, "flow": "xtls-rprx-vision"}
                for n, d in devices.items()]},
            "streamSettings": {
                "network": "raw", "security": "reality",
                "realitySettings": {
                    "show": False, "target": s["reality_target"], "xver": 0,
                    "serverNames": [s["reality_sni"]],
                    "privateKey": state["reality_private_key"],
                    "shortIds": [d["short_id"] for d in devices.values()] or [secrets.token_hex(8)],
                },
            },
        }],
        "outbounds": [
            {"tag": "direct", "protocol": "freedom", "settings": {
                "finalRules": [{"action": "block", "ip": DENY_CIDRS}]},
             "streamSettings": {"sockopt": {"domainStrategy": "UseIP"}}},
            {"tag": "blocked", "protocol": "blackhole"},
        ],
        "routing": {"domainStrategy": "IPOnDemand", "rules": [
            {"type": "field", "ip": DENY_CIDRS, "outboundTag": "blocked"},
        ]},
    }
    hy2 = {
        "listen": authority(s["bind"], s["hy2_port"]),
        # Hysteria rejects an empty user map. An unexported random sentinel keeps
        # an empty deployment running without granting any device credentials.
        "auth": {"type": "userpass", "userpass": {
            n: d["hy2_password"] for n, d in devices.items()} or {
                "_no_devices_": secrets.token_urlsafe(48)}},
        "ignoreClientBandwidth": True,
        "congestion": {"type": "bbr", "bbrProfile": "standard"},
        "disableUDP": False,
        "acl": {"inline": [f"reject({cidr})" for cidr in DENY_CIDRS] + ["direct(all)"]},
        "masquerade": {"type": "string", "string": {
            "content": "<!doctype html><html><title>Welcome</title><body><h1>Welcome</h1></body></html>",
            "headers": {"content-type": "text/html; charset=utf-8"}, "statusCode": 200}},
    }
    if s["hy2_mode"] == "acme":
        hy2["acme"] = {"domains": [s["hy2_domain"]], "email": s["acme_email"],
                       "ca": "letsencrypt", "type": "http", "dir": "/var/lib/hysteria"}
    else:
        hy2["tls"] = {"cert": "/etc/vpn/tls.crt", "key": "/etc/vpn/tls.key"}
    if s["obfs"] == "salamander":
        hy2["obfs"] = {"type": "salamander", "salamander": {"password": state["obfs_password"]}}
    write_json(root / "runtime/xray.json", xray, True)
    write_json(root / "runtime/hysteria.json", hy2, True)


def export_device(root: Path, state: dict, name: str) -> Path:
    device_name(name)
    d = state["devices"].get(name)
    if not d or not d["active"]:
        raise ValueError("Device does not exist or has been revoked.")
    s = state["settings"]
    destination = root / "exports" / name
    directory(root / "exports")
    reality = {"serverName": s["reality_sni"], "fingerprint": "chrome",
               "password": state["reality_public_key"], "shortId": d["short_id"]}
    client = {
        "log": {"loglevel": "warning", "access": "none"},
        "inbounds": [{"listen": "127.0.0.1", "port": 10808, "protocol": "socks",
                      "settings": {"auth": "noauth", "udp": True}}],
        "outbounds": [{"protocol": "vless", "settings": {"vnext": [{
            "address": s["endpoint"], "port": s["reality_port"], "users": [{
                "id": d["uuid"], "encryption": "none", "flow": "xtls-rprx-vision"}]}]},
            "streamSettings": {"network": "raw", "security": "reality", "realitySettings": reality},
            "mux": {"enabled": False}}],
    }
    tls = {"sni": tls_name(s), "insecure": False}
    if s["hy2_mode"] == "pinned":
        # Native Hysteria still enforces pinSHA256 when normal CA verification is disabled.
        tls.update(insecure=True, pinSHA256=fingerprint(root))
    hy2 = {"server": authority(s["endpoint"], s["hy2_port"]),
           "auth": f"{name}:{d['hy2_password']}", "tls": tls,
           "socks5": {"listen": "127.0.0.1:10809"}}
    if s["obfs"] == "salamander":
        hy2["obfs"] = {"type": "salamander", "salamander": {"password": state["obfs_password"]}}
    vquery = {"encryption": "none", "flow": "xtls-rprx-vision", "security": "reality",
              "sni": s["reality_sni"], "fp": "chrome", "pbk": state["reality_public_key"],
              "sid": d["short_id"], "type": "tcp"}
    hquery = {"sni": tls["sni"], "insecure": "1" if tls["insecure"] else "0"}
    if "pinSHA256" in tls:
        hquery["pinSHA256"] = tls["pinSHA256"]
    if s["obfs"] == "salamander":
        hquery.update(obfs="salamander", **{"obfs-password": state["obfs_password"]})
    links = (
        f"vless://{d['uuid']}@{authority(s['endpoint'], s['reality_port'])}?{urlencode(vquery)}#{quote(name + '-reality')}\n"
        f"hysteria2://{quote(hy2['auth'], safe='')}@{authority(s['endpoint'], s['hy2_port'])}/?{urlencode(hquery)}#{quote(name + '-hy2')}\n"
    )
    write_json(destination / "xray.json", client)
    write_json(destination / "hysteria.json", hy2)
    write_file(destination / "links.txt", links)
    return destination


def save_and_render(root: Path, state: dict) -> None:
    # Commit authoritative state first. A failed render is recoverable using `render`.
    write_json(root / ".state/server.json", state)
    render(root, state)
    for name, d in state["devices"].items():
        if d["active"]:
            export_device(root, state, name)


def doctor(root: Path, state: dict) -> None:
    s = state["settings"]
    validate_settings(s)
    expected = [root / ".state/server.json", root / "runtime/xray.json", root / "runtime/hysteria.json"]
    if s["hy2_mode"] == "pinned":
        expected += [root / "runtime/tls.key", root / "runtime/tls.crt"]
    for path in expected:
        if not path.is_file():
            raise ValueError(f"Missing {path}; run render (restore TLS files from backup if missing).")
        if os.name == "posix":
            mode = path.stat().st_mode & 0o777
            if mode & 0o007 or mode & 0o020:
                raise ValueError(f"Unsafe file permissions: {path.name}")
            if path.parent == root / "runtime" and (path.stat().st_gid != RUNTIME_GID or not mode & 0o040):
                raise ValueError(f"Container GID {RUNTIME_GID} cannot read {path}; run render with sudo and restore TLS file group/read permissions if needed.")
    runtime = root / "runtime"
    if os.name == "posix" and (runtime.stat().st_gid != RUNTIME_GID or runtime.stat().st_mode & 0o050 != 0o050):
        raise ValueError(f"Container GID {RUNTIME_GID} cannot traverse {runtime}; run render with sudo.")
    if s["hy2_mode"] == "pinned":
        cert = x509.load_pem_x509_certificate((root / "runtime/tls.crt").read_bytes())
        days = (cert.not_valid_after_utc - datetime.now(timezone.utc)).days
        if days < 30:
            raise ValueError(f"Pinned certificate expires in {days} days. Run rotate-certificate and reimport all profiles.")
    print("Local files checked. No network, container, DNS, or Great Firewall check was performed.")
    if os.name != "posix":
        print("Windows: POSIX file modes are not enforced; keep this folder private and initialize again on the VPS.")


def add_settings(parser: argparse.ArgumentParser, required: bool = False) -> None:
    parser.add_argument("--endpoint", required=required, help="VPS public IP or DNS, no scheme/port")
    parser.add_argument("--reality-target", required=required, help="TLS 1.3 / HTTP2 target host:443")
    parser.add_argument("--reality-sni", required=required, help="Name accepted by the REALITY target")
    parser.add_argument("--reality-port", type=int, default=443 if required else None)
    parser.add_argument("--hy2-port", type=int, default=443 if required else None)
    parser.add_argument("--bind", default="0.0.0.0" if required else None)
    parser.add_argument("--hy2-mode", choices=["pinned", "acme"], default="pinned" if required else None)
    parser.add_argument("--hy2-domain", default="" if required else None)
    parser.add_argument("--acme-email", default="" if required else None)
    parser.add_argument("--obfs", choices=["none", "salamander"], default="none" if required else None)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT, help="Deployment directory (default: script directory)")
    commands = parser.add_subparsers(dest="command", required=True)
    add_settings(commands.add_parser("init"), True)
    add_settings(commands.add_parser("configure"))
    for command in ("add-device", "revoke-device", "export"):
        commands.add_parser(command).add_argument("name")
    for command in ("render", "list-devices", "doctor", "rotate-certificate"):
        commands.add_parser(command)
    for command in ("check-deployment", "start"):
        commands.add_parser(command).add_argument("--registry", action="store_true",
                                                  help="Use compose.registry.yaml with authenticated GHCR pulls")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    try:
        if os.name == "posix" and os.geteuid() != 0:
            raise ValueError("Run with sudo on Linux so runtime files can be group-owned by container GID 10001.")
        with locked(root):
            if args.command == "init":
                if (root / ".state/server.json").exists():
                    raise ValueError("Already initialized. Use configure; existing credentials were preserved.")
                settings = {k: v for k, v in vars(args).items() if k not in ("command", "root")}
                validate_settings(settings)
                key = x25519.X25519PrivateKey.generate()
                state = {"version": 1, "settings": settings, "devices": {},
                         "reality_private_key": b64url(key.private_bytes_raw()),
                         "reality_public_key": b64url(key.public_key().public_bytes_raw()),
                         "obfs_password": secrets.token_urlsafe(32)}
                if settings["hy2_mode"] == "pinned":
                    generate_certificate(root, settings)
                save_and_render(root, state)
                print("Initialized. Add a device before starting services. Secrets are stored locally.")
                return 0
            state = load_state(root)
            if args.command == "configure":
                previous_name = tls_name(state["settings"])
                previous_mode = state["settings"]["hy2_mode"]
                state["settings"].update({k: v for k, v in vars(args).items()
                                          if k not in ("root", "command") and v is not None})
                validate_settings(state["settings"])
                if state["settings"]["hy2_mode"] == "pinned" and (
                    previous_mode != "pinned" or previous_name != tls_name(state["settings"])):
                    generate_certificate(root, state["settings"])
                    print("TLS pin changed. Reimport profiles on every active device.")
                save_and_render(root, state)
            elif args.command == "add-device":
                name = device_name(args.name)
                if name in state["devices"]:
                    raise ValueError("Name already used; choose a new name (revoked names are not reused).")
                state["devices"][name] = {"uuid": str(uuid.uuid4()), "short_id": secrets.token_hex(8),
                                          "hy2_password": secrets.token_urlsafe(32), "active": True}
                save_and_render(root, state)
                print(f"Created device; profiles: {root / 'exports' / name}")
            elif args.command == "revoke-device":
                name = device_name(args.name)
                if name not in state["devices"] or not state["devices"][name]["active"]:
                    raise ValueError("Device does not exist or is already revoked.")
                state["devices"][name]["active"] = False
                save_and_render(root, state)
                # Remove only the three files managed by this program; never recursively delete.
                for filename in ("xray.json", "hysteria.json", "links.txt"):
                    (root / "exports" / name / filename).unlink(missing_ok=True)
                print("Device revoked in generated configuration. Restart BOTH services to terminate existing sessions.")
            elif args.command == "export":
                print(export_device(root, state, args.name))
                return 0
            elif args.command == "list-devices":
                for name, d in state["devices"].items():
                    print(f"{name}\t{'active' if d['active'] else 'revoked'}")
                return 0
            elif args.command == "doctor":
                doctor(root, state)
                return 0
            elif args.command in ("check-deployment", "start"):
                from scripts.deployment import check, start
                doctor(root, state)
                if args.command == "start":
                    if not any(device["active"] for device in state["devices"].values()):
                        raise ValueError("Add a device before starting services.")
                    start(root, args.registry)
                else:
                    check(root, args.registry)
                return 0
            elif args.command == "rotate-certificate":
                if state["settings"]["hy2_mode"] != "pinned":
                    raise ValueError("ACME manages its own certificates; rotation only applies to pinned mode.")
                generate_certificate(root, state["settings"])
                save_and_render(root, state)
                print("TLS pin changed. Reimport profiles on every active device.")
            elif args.command == "render":
                save_and_render(root, state)
            print("Apply configuration: docker compose --profile hy2 restart xray hysteria")
        return 0
    except (ValueError, OSError, KeyError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
