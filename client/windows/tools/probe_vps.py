"""Opt-in native SOCKS probe; never changes Windows proxy, routes, DNS or firewall.

Reads private exported profiles, runs only owned core processes on loopback and
prints sanitized results. Native core output is bounded in memory, not files.
"""
from __future__ import annotations

import argparse
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import copy
import hashlib
import http.client
import json
from pathlib import Path
import socket
import ssl
import struct
import subprocess
import tempfile
import threading
import time
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[3]
PRIVATE = ROOT / ".state/client-test/45.77.179.227"
BINARIES = ROOT / ".cache/vps-probe/binaries"
EXPECTED_IP = "45.77.179.227"
LOCKS = {
    "Xray-windows-64.zip": "2e04a9995a6f2ad1b28c0bb25c708ecb2695e43a1c9c7889c4b6ac2b48cf7a6a",
    "hysteria-windows-amd64.exe": "162ef8fe55dc7ec810dda662908f8e65ec36bd6da39cb87c3bd66184ab68f067",
}


def verify_binaries() -> None:
    for name, digest in LOCKS.items():
        if hashlib.sha256((BINARIES / name).read_bytes()).hexdigest() != digest:
            raise RuntimeError("Pinned official core checksum mismatch.")
    with zipfile.ZipFile(BINARIES / "Xray-windows-64.zip") as archive:
        (BINARIES / "xray.exe").write_bytes(archive.read("xray.exe"))


def private_values(value):
    if isinstance(value, dict):
        for key, item in value.items():
            if key.lower() in {"id", "uuid", "auth", "password", "shortid", "pinsha256"} and isinstance(item, str):
                yield item
            else:
                yield from private_values(item)
    elif isinstance(value, list):
        for item in value:
            yield from private_values(item)


class Core:
    def __init__(self, command, secrets):
        self.lines = deque()
        self.byte_count = 0
        self.log_lock = threading.Lock()
        self.process = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

        def read():
            for raw in iter(self.process.stdout.readline, b""):
                line = raw.decode(errors="replace").strip()
                for secret in secrets:
                    if secret:
                        line = line.replace(secret, "[REDACTED]")
                line = line[:1024]
                with self.log_lock:
                    self.lines.append(line)
                    self.byte_count += len(line.encode())
                    while len(self.lines) > 1000 or self.byte_count > 1024 * 1024:
                        self.byte_count -= len(self.lines.popleft().encode())

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()

    def stop(self):
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)
        self.reader.join(timeout=1)
        self.process.stdout.close()

    def category(self):
        with self.log_lock:
            text = " ".join(self.lines).lower()
        if "pin" in text or "certificate" in text:
            return "TLS/pin error"
        if "auth" in text or "invalid user" in text:
            return "authentication error"
        if "timeout" in text or "timed out" in text:
            return "network/handshake timeout"
        return "core/network error"


def exact(stream, count):
    result = bytearray()
    while len(result) < count:
        data = stream.recv(count - len(result))
        if not data:
            raise ConnectionError("SOCKS stream closed")
        result.extend(data)
    return bytes(result)


def exit_ip(port, hostname="api.ipify.org", path="/?format=json"):
    with socket.create_connection(("127.0.0.1", port), timeout=8) as stream:
        stream.settimeout(10)
        stream.sendall(b"\x05\x01\x00")
        if exact(stream, 2) != b"\x05\x00":
            raise ConnectionError("SOCKS handshake rejected")
        host = hostname.encode("idna")
        stream.sendall(b"\x05\x01\x00\x03" + bytes([len(host)]) + host + struct.pack("!H", 443))
        reply = exact(stream, 4)
        if reply[1] != 0:
            raise ConnectionError("SOCKS destination rejected")
        length = {1: 4, 4: 16}.get(reply[3])
        if reply[3] == 3:
            length = exact(stream, 1)[0]
        if length is None:
            raise ConnectionError("Unexpected SOCKS reply")
        exact(stream, length + 2)
        with ssl.create_default_context().wrap_socket(stream, server_hostname=hostname) as tls:
            tls.sendall((f"GET {path} HTTP/1.1\r\nHost: {hostname}\r\nConnection: close\r\n\r\n").encode("ascii"))
            response = http.client.HTTPResponse(tls)
            response.begin()
            data = response.read(4096)
            if response.status != 200:
                raise ConnectionError("Exit check HTTP response was not 200")
            return json.loads(data)["ip"] if hostname == "api.ipify.org" else data.decode().strip()


@contextmanager
def core_client(protocol, profile, chained=False, bad_auth=False, fragment=False):
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    secrets = list(private_values(profile))
    if protocol == "xray":
        outbound = copy.deepcopy(profile["outbounds"][0])
        if outbound.get("protocol") != "vless":
            raise RuntimeError("Expected the exported VLESS profile")
        outbound["streamSettings"]["sockopt"] = {}
        configuration = {"log": {"loglevel": "warning"}, "inbounds": [
            {"listen": "127.0.0.1", "port": port, "protocol": "socks", "settings": {"udp": False}}],
            "outbounds": [outbound]}
        if bad_auth:
            outbound["settings"]["vnext"][0]["users"][0]["id"] = str(uuid.uuid4())
        if chained:
            outbound["streamSettings"]["sockopt"]["dialerProxy"] = "existing-vpn"
            configuration["outbounds"].append({"tag": "existing-vpn", "protocol": "http",
                "settings": {"servers": [{"address": "127.0.0.1", "port": 7688}]}})
        elif fragment:
            outbound["streamSettings"]["sockopt"]["dialerProxy"] = "fragment"
            configuration["outbounds"].append({"tag": "fragment", "protocol": "freedom",
                "settings": {"fragment": {"packets": "tlshello", "length": "100-200", "interval": "10-20"}}})
        binary = BINARIES / "xray.exe"
    else:
        configuration = {key: copy.deepcopy(profile[key]) for key in ("server", "auth", "tls", "obfs") if key in profile}
        if configuration.get("server") != EXPECTED_IP + ":443" or not configuration.get("tls", {}).get("pinSHA256"):
            raise RuntimeError("Expected the selected VPS and an explicit certificate pin")
        configuration["socks5"] = {"listen": f"127.0.0.1:{port}"}
        if bad_auth:
            configuration["tls"]["pinSHA256"] = "00" * 32
        binary = BINARIES / "hysteria-windows-amd64.exe"
    with tempfile.TemporaryDirectory(prefix="native-probe-", dir=PRIVATE) as temporary:
        folder = Path(temporary).resolve()
        if not folder.is_relative_to(PRIVATE.resolve()):
            raise RuntimeError("Invalid private working directory")
        config = folder / "client.json"
        config.write_text(json.dumps(configuration), encoding="utf-8")
        if protocol == "xray":
            validated = subprocess.run([str(binary), "run", "-test", "-config", str(config)],
                stdin=subprocess.DEVNULL, capture_output=True, timeout=10,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
            if validated.returncode:
                raise RuntimeError("Native Xray config validation failed; raw config output was suppressed")
            command = [str(binary), "run", "-config", str(config)]
        else:
            command = [str(binary), "client", "-c", str(config)]
        core = Core(command, secrets)
        try:
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                if core.process.poll() is not None:
                    raise RuntimeError(core.category())
                try:
                    with socket.create_connection(("127.0.0.1", port), timeout=.2):
                        break
                except OSError:
                    time.sleep(.1)
            else:
                raise RuntimeError(core.category())
            yield port, core
        finally:
            core.stop()


def probe(protocol, profile, chained=False):
    label = protocol + (" through existing HTTP proxy" if chained else " on existing OS route")
    started = time.monotonic()
    try:
        with core_client(protocol, profile, chained) as (port, core):
            observed = exit_ip(port)
            if observed != EXPECTED_IP:
                raise RuntimeError("Exit IP did not match the VPS")
            with ThreadPoolExecutor(max_workers=4) as executor:
                parallel = list(executor.map(lambda _: exit_ip(port), range(4)))
            if any(ip != EXPECTED_IP for ip in parallel):
                raise RuntimeError("Parallel exit IP mismatch")
        return {"test": label, "result": "PASS", "authenticated_exit": observed,
                "parallel_requests": 4, "seconds": round(time.monotonic() - started, 2)}
    except (OSError, RuntimeError, ValueError, subprocess.SubprocessError, http.client.HTTPException) as error:
        return {"test": label, "result": "FAIL", "error": type(error).__name__,
                "seconds": round(time.monotonic() - started, 2)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile-dir", type=Path, default=PRIVATE / "windows")
    args = parser.parse_args()
    verify_binaries()
    xray = json.loads((args.profile_dir / "xray.json").read_text(encoding="utf-8"))
    hy2 = json.loads((args.profile_dir / "hysteria.json").read_text(encoding="utf-8"))
    jobs = [("xray", xray, True), ("xray", xray, False), ("hysteria", hy2, False)]
    with ThreadPoolExecutor(max_workers=3) as executor:
        results = list(executor.map(lambda item: probe(*item), jobs))
    for result in results:
        print(json.dumps(result))
    print("Only owned loopback core processes were started and stopped. No system network settings were changed.")


if __name__ == "__main__":
    main()
