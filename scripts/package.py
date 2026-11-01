#!/usr/bin/env python3
"""Package an allowlist of source files only; never include server credentials."""
from pathlib import Path
import tarfile

root = Path(__file__).resolve().parents[1]
destination = root / "vpn-server-source.tar.gz"
files = ["README.md", "VALIDATION.md", "DEPLOY.md", "PUBLISH.md", "Dockerfile", "compose.yaml", "requirements.txt", "requirements-dev.txt", "VERSION", ".gitattributes",
         "vpnctl.py", ".dockerignore", ".gitignore", "scripts/host-tuning.sh", "scripts/package.py",
         "scripts/release_bundle.py", "scripts/container_smoke.py", "scripts/install-docker-ubuntu.sh",
         ".github/workflows/release.yml", "tests/test_vpnctl.py", "tests/test_release_bundle.py", "tests/integration.py"]
missing = [name for name in files if not (root / name).is_file()]
if missing:
    raise SystemExit("Missing source files: " + ", ".join(missing))
with tarfile.open(destination, "w:gz", format=tarfile.PAX_FORMAT) as archive:
    for name in files:
        path = root / name
        info = archive.gettarinfo(str(path), arcname=name)
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        info.mode = 0o755 if name.endswith(".sh") else 0o644
        with path.open("rb") as source:
            archive.addfile(info, source)
print(destination)
