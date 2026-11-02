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
import tarfile
import tempfile
import time

import yaml

from release_bundle import build_bundle

ROOT = Path(__file__).resolve().parents[1]


def run(*command, **kwargs):
    return subprocess.run(command, check=True, text=True, capture_output=True, **kwargs).stdout


def main():
    if sys.platform != "linux" or os.geteuid() != 0:
        raise SystemExit("Run as root on a Linux Docker host.")
    with tempfile.TemporaryDirectory(prefix="vpn-container-smoke-") as temporary:
        work = Path(temporary)
        base = work / "extracted release"
        base.mkdir()
        archive = build_bundle("example/private-vpn", "v0.0.0",
                               {"xray": "sha256:" + "a" * 64, "hysteria": "sha256:" + "b" * 64}, work / "assets")
        with tarfile.open(archive) as bundle:
            bundle.extractall(base, filter="data")
        source = yaml.safe_load((ROOT / "compose.yaml").read_text())
        tagged = []
        def cli(*args, ok=True):
            # Run the EXTRACTED CLI from an unrelated cwd, without --root.
            result = subprocess.run([sys.executable, str(base / "vpnctl.py"), *args],
                                    cwd=work, capture_output=True, text=True)
            if ok and result.returncode:
                raise RuntimeError(result.stderr)
            if not ok:
                assert result.returncode, "Uninitialized/missing config unexpectedly accepted"
            return result
        missing = cli("check-deployment", ok=False)
        assert str(base / ".state/server.json") in missing.stderr
        cli("init", "--endpoint", "127.0.0.1", "--reality-target", "example.com:443",
            "--reality-sni", "example.com")
        cli("add-device", "smoke")
        document = yaml.safe_load((base / "compose.yaml").read_text())
        document["name"] = "vpn-ci-" + str(os.getpid())
        document.pop("x-runtime", None)
        for name, service in document["services"].items():
            service["restart"] = "no"
            service["ports"] = []
            run("docker", "tag", source["services"][name]["image"], service["image"])
            tagged.append(service["image"])
        # Random host port on loopback only. Verify binding internal TCP443 as UID10001.
        document["services"]["xray"]["ports"] = ["127.0.0.1::443/tcp"]
        compose = base / "compose.yaml"
        compose.write_text(yaml.safe_dump(document, sort_keys=False))
        prefix = ("docker", "compose", "--project-directory", str(base), "-f", str(compose), "--profile", "hy2")
        try:
            # A missing host config must fail before a container is started.
            config = base / "runtime/xray.json"
            original = config.read_bytes()
            config.unlink()
            missing = cli("start", ok=False)
            assert str(config) in missing.stderr
            assert not run(*prefix, "ps", "--all", "--quiet").strip()
            cli("render")
            assert config.read_bytes() == original
            print(cli("start").stdout, end="")
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
            print("PASS: extracted Release from another cwd, missing-file recovery, nonroot TCP/UDP443, RO config permissions, writable ACME volume")
        except BaseException:
            # No logs printed: core errors can contain generated identifiers.
            print("Container smoke test failed. Inspect the isolated CI containers before cleanup.", file=sys.stderr)
            raise
        finally:
            run(*prefix, "down", "-v", "--remove-orphans")
            for tag in tagged:
                run("docker", "image", "rm", tag)


if __name__ == "__main__":
    main()
