#!/usr/bin/env python3
"""Build credential-free deployment bundles for GitHub Releases."""
from __future__ import annotations

import argparse
import copy
import hashlib
import io
import json
from pathlib import Path
import re
import tarfile

import yaml

ROOT = Path(__file__).resolve().parents[1]
FILES = ("vpnctl.py", "requirements.txt", "README.md", "DEPLOY.md",
         "scripts/install-docker-ubuntu.sh", "scripts/host-tuning.sh")


def validate(repository: str, version: str, digests: dict[str, str]) -> None:
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*", repository):
        raise ValueError("Repository must be owner/name.")
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", version):
        raise ValueError("Release version must be vMAJOR.MINOR.PATCH.")
    if set(digests) != {"xray", "hysteria"}:
        raise ValueError("Both Xray and Hysteria image digests are required.")
    for digest in digests.values():
        if not re.fullmatch(r"sha256:[a-f0-9]{64}", digest):
            raise ValueError("Every registry image needs an exact sha256 digest.")


def compose_files(source: dict, repository: str, version: str, digests: dict) -> tuple[dict, dict]:
    local, registry = copy.deepcopy(source), copy.deepcopy(source)
    for document in (local, registry):
        document.pop("x-runtime", None)
        for name, service in document["services"].items():
            service.pop("build", None)
            if document is local:
                service["image"] = f"personal-vpn/{name}:{version}-amd64"
                service["pull_policy"] = "never"
            else:
                service["image"] = f"ghcr.io/{repository.lower()}/{name}@{digests[name]}"
                service["pull_policy"] = "always"
    return local, registry


def add_bytes(archive: tarfile.TarFile, name: str, data: bytes) -> None:
    info = tarfile.TarInfo(name)
    info.mode = 0o755 if name.endswith(".sh") else 0o644
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    info.mtime = 0
    info.size = len(data)
    archive.addfile(info, io.BytesIO(data))


def build_bundle(repository: str, version: str, digests: dict, output: Path,
                 root: Path = ROOT) -> Path:
    validate(repository, version, digests)
    source = yaml.safe_load((root / "compose.yaml").read_text(encoding="utf-8"))
    local, registry = compose_files(source, repository, version, digests)
    contents = {name: (root / name).read_bytes() for name in FILES}
    contents.update({
        "compose.yaml": yaml.safe_dump(local, sort_keys=False).encode(),
        "compose.registry.yaml": yaml.safe_dump(registry, sort_keys=False).encode(),
        "release.json": (json.dumps({"repository": repository, "version": version,
                                    "archive_platform": "linux/amd64", "images": digests}, indent=2) + "\n").encode(),
    })
    output.mkdir(parents=True, exist_ok=True)
    path = output / f"personal-vpn-{version}.tar.gz"
    with tarfile.open(path, "w:gz", format=tarfile.PAX_FORMAT) as archive:
        for name, data in contents.items():
            add_bytes(archive, name, data)
    return path


def checksums(output: Path) -> Path:
    assets = sorted(output.glob("*.tar.gz"))
    if not assets:
        raise ValueError("No release archives were produced.")
    lines = []
    for path in assets:
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        lines.append(f"{digest}  {path.name}\n")
    destination = output / "SHA256SUMS"
    destination.write_text("".join(lines), encoding="utf-8", newline="\n")
    return destination


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--xray-digest", required=True)
    parser.add_argument("--hysteria-digest", required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "dist")
    args = parser.parse_args()
    print(build_bundle(args.repository, args.version,
                       {"xray": args.xray_digest, "hysteria": args.hysteria_digest}, args.output))
    print(checksums(args.output))


if __name__ == "__main__":
    main()
