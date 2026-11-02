"""Fetch only the exported client profiles using the restricted test SSH key."""
from __future__ import annotations

import argparse
import io
import json
from pathlib import Path
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[3]
PRIVATE = ROOT / ".state/client-test/45.77.179.227"
ALLOWED = {"xray.json", "hysteria.json", "links.txt"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--expected-host-fingerprint", required=True)
    args = parser.parse_args()
    known_hosts = PRIVATE / "known_hosts"
    result = subprocess.run(["ssh-keygen", "-lf", str(known_hosts), "-E", "sha256"],
                            check=True, capture_output=True, text=True)
    if not any(line.split()[1] == args.expected_host_fingerprint for line in result.stdout.splitlines()):
        raise RuntimeError("SSH host fingerprint mismatch; no credential was sent.")
    bridge = Path(__file__).with_name("http_connect.py")
    python = ROOT / ".tools/release-venv/Scripts/python.exe"
    proxy = f'ProxyCommand="{python.as_posix()}" "{bridge.as_posix()}" %h %p'
    command = ["ssh", "-F", str(PRIVATE / "empty_ssh_config"), "-i", str(PRIVATE / "profile_fetch_ed25519"),
               "-o", proxy, "-o", f"UserKnownHostsFile={known_hosts}", "-o", "GlobalKnownHostsFile=NUL",
               "-o", "HostKeyAlgorithms=ssh-ed25519", "-o", "StrictHostKeyChecking=yes",
               "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
               "root@45.77.179.227", "fetch-profile"]
    response = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, timeout=30,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if response.returncode:
        raise RuntimeError(f"Restricted profile fetch failed (SSH exit {response.returncode}); no profile was printed.")
    if len(response.stdout) > 256 * 1024:
        raise RuntimeError("Unexpected profile archive size.")
    with tarfile.open(fileobj=io.BytesIO(response.stdout), mode="r:gz") as archive:
        members = archive.getmembers()
        if len(members) != 3 or {member.name for member in members} != ALLOWED:
            raise RuntimeError("Unexpected profile archive members.")
        if any(not member.isfile() or member.size > 65536 for member in members):
            raise RuntimeError("Unexpected profile member type or size.")
        content = {member.name: archive.extractfile(member).read() for member in members}
    # Validate privately before writing, without including contents in errors.
    for filename in ("xray.json", "hysteria.json"):
        try:
            json.loads(content[filename])
        except (ValueError, UnicodeError):
            raise RuntimeError("Invalid client JSON in the exported profile.") from None
    destination = PRIVATE / "windows"
    destination.mkdir(exist_ok=True)
    for filename, data in content.items():
        (destination / filename).write_bytes(data)
    print(f"PASS: fetched 3 client files privately into {destination}")
    print("No server state, TLS private key or credential contents were printed.")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, subprocess.SubprocessError, tarfile.TarError) as error:
        raise SystemExit(str(error)) from None
