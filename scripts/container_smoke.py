#!/usr/bin/env python3
"""Exercise actual scratch images, low ports, group permissions and ACME volume.

Requires a Linux Docker host and the two local images from compose.yaml.
Never uses existing deployment state, starts the production Compose project,
changes a firewall, or publishes ports on public interfaces.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time

import yaml

ROOT = Path(__file__).resolve().parents[1]


def run(*command, **kwargs):
    return subprocess.run(command, check=True, text=True, capture_output=True, **kwargs).stdout


def main():
    if sys.platform != "linux" or os.geteuid() != 0:
        raise SystemExit("Run as root on a Linux Docker host.")
    with tempfile.TemporaryDirectory(prefix="vpn-container-smoke-") as temporary:
        base = Path(temporary)
        def cli(*args):
            run(sys.executable, str(ROOT / "vpnctl.py"), "--root", str(base), *args)
        cli("init", "--endpoint", "127.0.0.1", "--reality-target", "example.com:443",
            "--reality-sni", "example.com")
        cli("add-device", "smoke")
        document = yaml.safe_load((ROOT / "compose.yaml").read_text())
        document["name"] = "vpn-ci-" + str(os.getpid())
        document.pop("x-runtime", None)
        for service in document["services"].values():
            service.pop("build", None)
            service.pop("profiles", None)
            service["restart"] = "no"
            service["ports"] = []
            for volume in service["volumes"]:
                if volume["type"] == "bind":
                    volume["source"] = str(base / "runtime")
        # Random host port on loopback only. Verify binding internal TCP443 as UID10001.
        document["services"]["xray"]["ports"] = ["127.0.0.1::443/tcp"]
        compose = base / "compose.json"
        compose.write_text(json.dumps(document))
        prefix = ("docker", "compose", "-f", str(compose))
        try:
            run(*prefix, "up", "-d", "--no-build", "--pull", "never")
            for name in ("xray", "hysteria"):
                identifier = run(*prefix, "ps", "-q", name).strip()
                assert identifier, f"Missing {name} container"
                for _ in range(30):
                    record = json.loads(run("docker", "inspect", identifier))[0]
                    if record["State"]["Status"] == "running":
                        break
                    time.sleep(0.1)
                assert record["Config"]["User"] == "10001:10001"
                assert record["HostConfig"]["ReadonlyRootfs"]
                assert "ALL" in record["HostConfig"]["CapDrop"]
                time.sleep(0.4)
                assert json.loads(run("docker", "inspect", identifier))[0]["State"]["Running"], name
                if name == "hysteria":
                    logs = subprocess.run(["docker", "logs", identifier], check=True,
                                          text=True, stdout=subprocess.PIPE,
                                          stderr=subprocess.STDOUT).stdout
                    assert "server up and running" in logs.lower(), "Hysteria didn't bind UDP443"
            address = run(*prefix, "port", "xray", "443").strip()
            with socket.create_connection(("127.0.0.1", int(address.rsplit(":", 1)[1])), timeout=5):
                pass
            # A successful low-port Hysteria start verified UID and config permissions.
            # Test the fresh ACME volume's ownership using a separate ephemeral helper.
            volume = next(m["Name"] for m in json.loads(run("docker", "inspect",
                run(*prefix, "ps", "-q", "hysteria").strip()))[0]["Mounts"] if m["Type"] == "volume")
            run("docker", "run", "--rm", "--user", "10001:10001", "--mount",
                f"type=volume,source={volume},target=/state",
                "alpine:3.22.2@sha256:4b7ce07002c69e8f3d704a9c5d6fd3053be500b7f1c69fc0d80990c2ad8dd412", "sh", "-c",
                "test -w /state && touch /state/.ci-write-check && rm /state/.ci-write-check")
            print("PASS: scratch container startup, nonroot TCP/UDP443, RO config permissions, writable ACME volume")
        except BaseException:
            # No logs printed: core errors can contain generated identifiers.
            print("Container smoke test failed. Inspect the isolated CI containers before cleanup.", file=sys.stderr)
            raise
        finally:
            run(*prefix, "down", "-v", "--remove-orphans")


if __name__ == "__main__":
    main()
