"""Anchored Compose commands and mount checks. No credentials are printed."""
from __future__ import annotations

import json
from pathlib import Path
import shlex
import subprocess


def command(root: Path, registry: bool = False) -> list[str]:
    root = root.resolve()
    compose = root / ("compose.registry.yaml" if registry else "compose.yaml")
    if not compose.is_file():
        raise ValueError(f"Missing {compose}. Extract the deployment bundle into this directory.")
    return ["docker", "compose", "--project-directory", str(root), "-f", str(compose), "--profile", "hy2"]


def run(arguments: list[str], stage: str) -> str:
    try:
        result = subprocess.run(arguments, stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=120)
    except FileNotFoundError:
        raise ValueError("Docker was not found. Run scripts/install-docker-ubuntu.sh first.") from None
    except subprocess.TimeoutExpired:
        raise ValueError(f"{stage} timed out. Check Docker status and image availability on the VPS.") from None
    if result.returncode:
        # Core validation errors may contain configuration values: keep them local.
        raise ValueError(f"{stage} failed (exit {result.returncode}). Run on the VPS for details: {shlex.join(arguments)}")
    return result.stdout


def check_mounts(document: dict, root: Path) -> None:
    expected = (root / "runtime").resolve()
    for name in ("xray", "hysteria"):
        service = document.get("services", {}).get(name, {})
        if service.get("user") != "10001:10001":
            raise ValueError(f"{name}: expected container user 10001:10001.")
        mounts = [mount for mount in service.get("volumes", []) if mount.get("target") == "/etc/vpn"]
        if len(mounts) != 1 or mounts[0].get("type") != "bind":
            raise ValueError(f"{name}: expected a bind mount from {expected} to /etc/vpn.")
        mount = mounts[0]
        if Path(mount.get("source", "")).resolve() != expected or not mount.get("read_only"):
            raise ValueError(f"{name}: /etc/vpn must mount {expected} read-only. Check the selected Compose file.")


def check(root: Path, registry: bool = False) -> list[str]:
    prefix = command(root, registry)
    document = json.loads(run(prefix + ["config", "--format", "json"], "Compose configuration check"))
    check_mounts(document, root)
    if registry:
        run(prefix + ["pull"], "Private GHCR image pull")
    else:
        for name in ("xray", "hysteria"):
            image = document["services"][name]["image"]
            run(["docker", "image", "inspect", image], f"Local {name} image check (load the release image archive first)")
    run(prefix + ["run", "--rm", "--no-deps", "--interactive=false", "--pull", "never", "xray",
                  "run", "-test", "-config", "/etc/vpn/xray.json"], "Xray container config/mount check")
    print(f"PASS: both Compose mounts use {root.resolve() / 'runtime'} -> /etc/vpn (read-only).")
    print("PASS: Xray read and validated /etc/vpn/xray.json inside the container as UID 10001.")
    return prefix


def verify_running(prefix: list[str], root: Path) -> None:
    for name in ("xray", "hysteria"):
        identifier = run(prefix + ["ps", "--all", "--quiet", name], f"Find {name} container").strip()
        if not identifier:
            raise ValueError(f"{name} container was not created.")
        record = json.loads(run(["docker", "inspect", identifier], f"Inspect {name} container"))[0]
        mount = [item for item in record["Mounts"] if item["Destination"] == "/etc/vpn"]
        if (len(mount) != 1 or mount[0]["Type"] != "bind" or mount[0]["RW"]
                or Path(mount[0]["Source"]).resolve() != (root / "runtime").resolve()):
            raise ValueError(f"{name}: running container has the wrong /etc/vpn mount.")
        if not record["State"]["Running"]:
            raise ValueError(f"{name} exited. Inspect its local Compose logs.")
        if record["Config"]["User"] != "10001:10001":
            raise ValueError(f"{name}: running container has an unexpected user.")
    print("PASS: Xray and Hysteria containers are running with the expected config bind mounts.")


def start(root: Path, registry: bool = False) -> None:
    prefix = check(root, registry)
    run(prefix + ["up", "-d", "--no-build", "--pull", "never", "--force-recreate",
                  "--wait", "--wait-timeout", "30"], "Container startup")
    verify_running(prefix, root)
    print("This checks local startup and mounts; test the tunnel from your client next.")
