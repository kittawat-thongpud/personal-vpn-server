"""Native core integration test, using loopback fixtures rather than the internet.

Run: python tests/integration.py --download
Downloads pinned upstream Windows x64 or Linux x64/arm64 executables into .cache.
Every download is verified against the locked release SHA256 before execution.
This exercises core interoperability, NOT Docker, VPS throughput, TUN, or the GFW.
Production private-address blocks are checked first. Only temporary test configs
then receive narrowly scoped loopback exceptions for the HTTP and UDP fixtures.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import platform
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import uuid
import zipfile

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

PROJECT = Path(__file__).resolve().parents[1]
CACHE = PROJECT / ".cache" / "integration"
PAYLOAD = b"personal-vpn-parallel-stream-test\n" * 8192
ASSETS = {
    "windows-amd64": (
        ("XTLS/Xray-core", "v26.10.31", "Xray-windows-64.zip", "2e04a9995a6f2ad1b28c0bb25c708ecb2695e43a1c9c7889c4b6ac2b48cf7a6a"),
        ("HyNetworks/hysteria", "app/v2.13.0", "hysteria-windows-amd64.exe", "162ef8fe55dc7ec810dda662908f8e65ec36bd6da39cb87c3bd66184ab68f067"),
    ),
    "linux-amd64": (
        ("XTLS/Xray-core", "v26.10.31", "Xray-linux-64.zip", "e4bd9119f50ae7c41658b1abd0679ae06f98bd864a2907ff33f2f2cf7081b214"),
        ("HyNetworks/hysteria", "app/v2.13.0", "hysteria-linux-amd64", "907ba8c9693edb104b20582681fb7dc15639d5b64a9cbb616a7b539190a86691"),
    ),
    "linux-arm64": (
        ("XTLS/Xray-core", "v26.10.31", "Xray-linux-arm64-v8a.zip", "1811c8f1da0c3b6dc707ebaf1c8390e987ba39859a8f0a7aadd8526157f2ec8d"),
        ("HyNetworks/hysteria", "app/v2.13.0", "hysteria-linux-arm64", "a68a61a84452ca250ce0368202521965ca9cc9d801a404f1dc9008ac6cf677a7"),
    ),
}


def json_read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def json_write(path, data):
    Path(path).write_text(json.dumps(data), encoding="utf-8")


def binaries(download):
    machine = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    key = f"{platform.system().lower()}-{machine}"
    if key not in ASSETS:
        raise RuntimeError(f"Unsupported integration platform: {key}")
    folder = CACHE / key
    folder.mkdir(parents=True, exist_ok=True)
    results = []
    for repository, tag, filename, expected in ASSETS[key]:
        path = folder / filename
        if not path.exists():
            if not download:
                raise RuntimeError("Pinned binaries absent; rerun with --download")
            url = f"https://github.com/{repository}/releases/download/{tag}/{filename}"
            request = urllib.request.Request(url, headers={"User-Agent": "personal-vpn-integration"})
            with urllib.request.urlopen(request, timeout=60) as source:
                content = source.read()
            if hashlib.sha256(content).hexdigest() != expected:
                raise RuntimeError(f"SHA256 mismatch: {filename}")
            path.write_bytes(content)
        if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise RuntimeError(f"Cached SHA256 mismatch: {filename}")
        if filename.endswith(".zip"):
            member = "xray.exe" if key.startswith("windows") else "xray"
            executable = folder / member
            # Re-extract from verified archive, so tampering with a cached EXE
            # cannot bypass archive integrity validation.
            with zipfile.ZipFile(path) as archive:
                executable.write_bytes(archive.read(member))
        else:
            executable = path
        executable.chmod(0o700)
        results.append(executable)
    return results


def free_port(kind=socket.SOCK_STREAM):
    with socket.socket(socket.AF_INET, kind) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def exact(sock, count):
    result = b""
    while len(result) < count:
        data = sock.recv(count - len(result))
        if not data:
            raise ConnectionError("Tunnel closed")
        result += data
    return result


def socks_request(proxy, target, command=1):
    sock = socket.create_connection(("127.0.0.1", proxy), timeout=4)
    sock.settimeout(4)
    try:
        sock.sendall(b"\x05\x01\x00")
        if exact(sock, 2) != b"\x05\x00":
            raise ConnectionError("SOCKS authentication negotiation failed")
        sock.sendall(bytes((5, command, 0, 1)) + socket.inet_aton("127.0.0.1") + struct.pack("!H", target))
        reply = exact(sock, 4)
        if reply[1] != 0:
            raise ConnectionError("SOCKS request rejected")
        if reply[3] == 1:
            address = socket.inet_ntoa(exact(sock, 4))
        elif reply[3] == 3:
            address = exact(sock, exact(sock, 1)[0]).decode()
        elif reply[3] == 4:
            address = socket.inet_ntop(socket.AF_INET6, exact(sock, 16))
        else:
            raise ConnectionError("Invalid SOCKS address")
        port = struct.unpack("!H", exact(sock, 2))[0]
        return sock, (address, port)
    except BaseException:
        sock.close()
        raise


def fetch(proxy, target):
    sock, _ = socks_request(proxy, target)
    with sock:
        sock.sendall(b"GET /payload HTTP/1.1\r\nHost: fixture.test\r\nConnection: close\r\n\r\n")
        data = b""
        while True:
            part = sock.recv(65536)
            if not part:
                break
            data += part
        header, separator, body = data.partition(b"\r\n\r\n")
        if not separator or b" 200 " not in header or body != PAYLOAD:
            raise ConnectionError("Tunnel did not return fixture payload")
        return len(body)


def must_fail(action, description):
    try:
        action()
    except (ConnectionError, OSError, TimeoutError):
        return
    raise AssertionError(f"Unexpected success: {description}")


def udp_echo(proxy, target):
    control, relay = socks_request(proxy, 0, command=3)
    with control, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as datagram:
        datagram.settimeout(4)
        if relay[0] in ("0.0.0.0", "::"):
            relay = ("127.0.0.1", relay[1])
        payload = b"personal-vpn-udp-roundtrip"
        request = b"\x00\x00\x00\x01" + socket.inet_aton("127.0.0.1") + struct.pack("!H", target) + payload
        datagram.sendto(request, relay)
        response, _ = datagram.recvfrom(2048)
        if response[:3] != b"\x00\x00\x00" or response[3] != 1 or response[10:] != payload:
            raise ConnectionError("SOCKS UDP echo did not match payload")


@contextmanager
def udp_fixture():
    endpoint = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    endpoint.bind(("127.0.0.1", 0))
    endpoint.settimeout(0.2)
    stop = threading.Event()
    def echo():
        while not stop.is_set():
            try:
                data, sender = endpoint.recvfrom(2048)
                endpoint.sendto(data, sender)
            except socket.timeout:
                continue
            except OSError:
                break
    worker = threading.Thread(target=echo, daemon=True)
    worker.start()
    try:
        yield endpoint.getsockname()[1]
    finally:
        stop.set()
        worker.join(timeout=1)
        endpoint.close()


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Length", str(len(PAYLOAD)))
        self.end_headers()
        try:
            self.wfile.write(PAYLOAD)
        except (OSError, ssl.SSLError):
            pass

    def log_message(self, *args):
        pass


@contextmanager
def fixture(root, tls=False):
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    if tls:
        key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")])
        now = datetime.now(timezone.utc)
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(now - timedelta(minutes=1)).not_valid_after(now + timedelta(days=1))
                .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost")]), False)
                .sign(key, hashes.SHA256()))
        (root / "target.crt").write_bytes(cert.public_bytes(serialization.Encoding.PEM))
        (root / "target.key").write_bytes(key.private_bytes(serialization.Encoding.PEM,
                                          serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.minimum_version = ssl.TLSVersion.TLSv1_3
        context.set_ecdh_curve("X25519")
        context.set_alpn_protocols(["h2", "http/1.1"])
        context.load_cert_chain(root / "target.crt", root / "target.key")
        # Wrap accepted sockets in worker threads. REALITY deliberately does not
        # finish some target handshakes, which would block accept() if the
        # listening socket itself were SSL wrapped.
        original_finish = server.finish_request
        def tls_finish(request, client_address):
            request.settimeout(3)
            try:
                with context.wrap_socket(request, server_side=True) as wrapped:
                    original_finish(wrapped, client_address)
            except (OSError, ssl.SSLError):
                pass
        server.finish_request = tls_finish
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        yield server.server_address[1]
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=2)


class Processes:
    def __init__(self, root):
        self.root, self.items = root, []

    def start(self, label, command):
        log = open(self.root / f"{label}.log", "wb")
        process = subprocess.Popen([str(c) for c in command], stdout=log, stderr=subprocess.STDOUT,
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.items.append((process, log))
        return process

    def close(self):
        for process, _ in reversed(self.items):
            if process.poll() is None:
                process.terminate()
        for process, log in self.items:
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
            log.close()
        self.items.clear()


def ready(process, port):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError("Core exited before SOCKS listener became ready")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.05)
    raise RuntimeError("SOCKS listener was not ready within 5 seconds")


def run(download):
    xray, hy2 = binaries(download)
    with tempfile.TemporaryDirectory(prefix="vpn-native-integration-") as temp:
        root = Path(temp)
        processes = Processes(root)
        secrets = []
        def cli(*arguments):
            result = subprocess.run([sys.executable, str(PROJECT / "vpnctl.py"), "--root", str(root), *arguments],
                                    capture_output=True, text=True, timeout=15)
            if result.returncode:
                raise RuntimeError(result.stderr)
        try:
            with fixture(root) as http_port, fixture(root, tls=True) as tls_port, udp_fixture() as echo_port:
                tcp_port, udp_port = free_port(), free_port(socket.SOCK_DGRAM)
                cli("init", "--endpoint", "127.0.0.1", "--reality-target", f"127.0.0.1:{tls_port}",
                    "--reality-sni", "localhost", "--bind", "127.0.0.1",
                    "--reality-port", str(tcp_port), "--hy2-port", str(udp_port))
                def empty_startup(label):
                    xs = json_read(root / "runtime/xray.json")
                    hs = json_read(root / "runtime/hysteria.json")
                    hs["tls"] = {"cert": str(root / "runtime/tls.crt"), "key": str(root / "runtime/tls.key")}
                    json_write(root / "empty-x.json", xs)
                    json_write(root / "empty-h.json", hs)
                    xp = processes.start(label + "-x", [xray, "run", "-config", root / "empty-x.json"])
                    hp = processes.start(label + "-h", [hy2, "server", "-c", root / "empty-h.json"])
                    time.sleep(0.5)
                    if xp.poll() is not None or hp.poll() is not None:
                        raise RuntimeError("Empty device configuration could not start both cores")
                    if "server up and running" not in (root / f"{label}-h.log").read_text(errors="replace"):
                        raise RuntimeError("Empty device Hysteria server did not become ready")
                    processes.close()
                empty_startup("initial-empty")
                print("PASS: initial empty device configurations start both cores")
                cli("add-device", "alice")
                cli("add-device", "bob")
                state = json_read(root / ".state/server.json")
                secrets.extend([state["reality_private_key"], state["obfs_password"]])
                for device in state["devices"].values():
                    secrets.extend(device[key] for key in ("uuid", "short_id", "hy2_password"))
                xserver = json_read(root / "runtime/xray.json")
                xserver["log"]["loglevel"] = "debug"
                hserver = json_read(root / "runtime/hysteria.json")
                hserver["tls"] = {"cert": str(root / "runtime/tls.crt"), "key": str(root / "runtime/tls.key")}
                xconfig, hconfig = root / "xserver.json", root / "hserver.json"
                json_write(xconfig, xserver)
                json_write(hconfig, hserver)
                check = subprocess.run([str(xray), "run", "-test", "-config", str(xconfig)], capture_output=True, timeout=15)
                if check.returncode:
                    raise RuntimeError("Xray configuration validation failed: " +
                                       (check.stdout + check.stderr).decode(errors="replace"))
                print("PASS: generated Xray configuration accepted by pinned core")
                xc = json_read(root / "exports/alice/xray.json")
                xc["log"]["loglevel"] = "debug"
                hc = json_read(root / "exports/alice/hysteria.json")
                xp, hp = free_port(), free_port()
                xc["inbounds"][0]["port"] = xp
                hc["socks5"]["listen"] = f"127.0.0.1:{hp}"
                xclient, hclient = root / "xclient.json", root / "hclient.json"
                json_write(xclient, xc)
                json_write(hclient, hc)

                def start_servers():
                    processes.start("xserver", [xray, "run", "-config", xconfig])
                    processes.start("hserver", [hy2, "server", "-c", hconfig])
                    time.sleep(0.3)

                def start_clients():
                    process = processes.start("xclient", [xray, "run", "-config", xclient])
                    ready(process, xp)
                    process = processes.start("hclient", [hy2, "client", "-c", hclient])
                    ready(process, hp)

                start_servers()
                start_clients()
                must_fail(lambda: fetch(xp, http_port), "production Xray private egress block")
                must_fail(lambda: fetch(hp, http_port), "production Hysteria private egress block")
                must_fail(lambda: udp_echo(xp, echo_port), "production Xray private UDP egress block")
                must_fail(lambda: udp_echo(hp, echo_port), "production Hysteria private UDP egress block")
                print("PASS: both production configurations reject private fixture destination")
                processes.close()

                # Explicitly permit only this fixture in temporary server configs.
                xserver["routing"]["rules"] = []
                xserver["outbounds"][0]["settings"]["finalRules"].insert(0, {
                    "action": "allow", "network": "tcp", "ip": ["127.0.0.1/32"], "port": http_port})
                xserver["outbounds"][0]["settings"]["finalRules"].insert(0, {
                    "action": "allow", "network": "udp", "ip": ["127.0.0.1/32"], "port": echo_port})
                hserver["acl"]["inline"].insert(0, f"direct(127.0.0.1, tcp/{http_port})")
                hserver["acl"]["inline"].insert(0, f"direct(127.0.0.1, udp/{echo_port})")
                json_write(xconfig, xserver)
                json_write(hconfig, hserver)
                start_servers()
                start_clients()
                for name, proxy in (("REALITY", xp), ("Hysteria2", hp)):
                    fetch(proxy, http_port)
                    with ThreadPoolExecutor(max_workers=8) as pool:
                        counts = list(pool.map(lambda _: fetch(proxy, http_port), range(8)))
                    assert counts == [len(PAYLOAD)] * 8
                    print(f"PASS: {name} authenticated SOCKS tunnel, 8 parallel complete transfers")
                    udp_echo(proxy, echo_port)
                    print(f"PASS: {name} authenticated SOCKS UDP roundtrip")
                processes.close()

                # Pin failure must prevent starting a usable native Hysteria proxy.
                start_servers()
                hc["tls"]["pinSHA256"] = "00" * 32
                json_write(hclient, hc)
                bad_pin = processes.start("badpin", [hy2, "client", "-c", hclient])
                try:
                    ready(bad_pin, hp)
                except RuntimeError:
                    pass
                must_fail(lambda: fetch(hp, http_port), "incorrect Hysteria TLS certificate pin")
                print("PASS: incorrect Hysteria certificate pin rejected")
                processes.close()
                hc = json_read(root / "exports/alice/hysteria.json")
                hc["socks5"]["listen"] = f"127.0.0.1:{hp}"

                # Wrong credentials must fail even with the valid public key / pin.
                start_servers()
                xc["outbounds"][0]["settings"]["vnext"][0]["users"][0]["id"] = str(uuid.uuid4())
                hc["auth"] = "alice:incorrect-password"
                json_write(xclient, xc)
                json_write(hclient, hc)
                process = processes.start("wrong-xauth", [xray, "run", "-config", xclient])
                ready(process, xp)
                process = processes.start("wrong-hauth", [hy2, "client", "-c", hclient])
                try:
                    ready(process, hp)
                except RuntimeError:
                    pass
                must_fail(lambda: fetch(xp, http_port), "incorrect VLESS UUID")
                must_fail(lambda: fetch(hp, http_port), "incorrect Hysteria password")
                print("PASS: wrong UUID and password rejected")
                processes.close()

                # Keep Alice's old files, revoke, regenerate/restart both servers.
                xc = json_read(root / "exports/alice/xray.json")
                hc = json_read(root / "exports/alice/hysteria.json")
                xc["inbounds"][0]["port"] = xp
                hc["socks5"]["listen"] = f"127.0.0.1:{hp}"
                json_write(xclient, xc)
                json_write(hclient, hc)
                cli("revoke-device", "alice")
                xserver = json_read(root / "runtime/xray.json")
                hserver = json_read(root / "runtime/hysteria.json")
                xserver["routing"]["rules"] = []
                xserver["outbounds"][0]["settings"]["finalRules"].insert(0, {
                    "action": "allow", "network": "tcp", "ip": ["127.0.0.1/32"], "port": http_port})
                hserver["acl"]["inline"].insert(0, f"direct(127.0.0.1, tcp/{http_port})")
                hserver["tls"] = {"cert": str(root / "runtime/tls.crt"), "key": str(root / "runtime/tls.key")}
                json_write(xconfig, xserver)
                json_write(hconfig, hserver)
                start_servers()
                process = processes.start("revoked-x", [xray, "run", "-config", xclient])
                ready(process, xp)
                process = processes.start("revoked-h", [hy2, "client", "-c", hclient])
                try:
                    ready(process, hp)
                except RuntimeError:
                    pass
                must_fail(lambda: fetch(xp, http_port), "revoked VLESS device")
                must_fail(lambda: fetch(hp, http_port), "revoked Hysteria device")
                print("PASS: revoked device rejected by both restarted cores")
                processes.close()

                # Ensure successful clients still work after another is revoked.
                xc = json_read(root / "exports/bob/xray.json")
                hc = json_read(root / "exports/bob/hysteria.json")
                xc["inbounds"][0]["port"] = xp
                hc["socks5"]["listen"] = f"127.0.0.1:{hp}"
                json_write(xclient, xc)
                json_write(hclient, hc)
                start_servers()
                start_clients()
                fetch(xp, http_port)
                fetch(hp, http_port)
                print("PASS: other device remains usable after revocation")
                processes.close()
                cli("revoke-device", "bob")
                empty_startup("last-revoked")
                print("PASS: both cores start safely after the last device is revoked")
        except BaseException:
            processes.close()
            # Core logs may include identifiers; redact all generated credentials.
            for path in sorted(root.glob("*.log")):
                output = path.read_text(encoding="utf-8", errors="replace")[-11000:]
                for secret in secrets:
                    output = output.replace(secret, "[redacted]")
                print(f"--- {path.name} ---\n{output}", file=sys.stderr)
            raise
        finally:
            processes.close()
    print("Native integration passed. Docker and China network behavior remain untested.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true", help="Fetch pinned native test binaries")
    arguments = parser.parse_args()
    run(arguments.download)
