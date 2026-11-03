"""Publish one qualified client bundle; never overwrite release/source history."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


def digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def required_assets(version: str) -> set[str]:
    return {f"PersonalVPN-Android-{version}.apk", "ANDROID-SIGNATURE.txt"} | {
        name for arch in ("x86", "x64") for name in (
            f"PersonalVPN-Setup-{version}-{arch}.exe",
            f"PersonalVPN-windows-{arch}-{version}.zip",
        )
    }


def verify_bundle(root: Path, version: str) -> list[dict]:
    required = required_assets(version)
    if not required <= {path.name for path in root.iterdir() if path.is_file()}:
        raise ValueError("A complete Android + Windows x86 + Windows x64 bundle is required")
    verified = set()
    for architecture in ("x86", "x64", "android"):
        checksum_file = root / f"SHA256SUMS-{architecture}.txt"
        for line in checksum_file.read_text(encoding="utf-8-sig").splitlines():
            expected, filename = line.split(maxsplit=1)
            filename = filename.removeprefix("./")
            if Path(filename).name != filename or not re.fullmatch(r"[a-fA-F0-9]{64}", expected):
                raise ValueError("Unsafe or invalid platform checksum entry")
            path = root / filename
            if not path.is_file() or path.stat().st_size == 0 or digest(path) != expected.lower():
                raise ValueError(f"Platform checksum mismatch: {filename}")
            verified.add(filename)
    if not required <= verified:
        raise ValueError("Every installer must be covered by a platform checksum")
    allowed = required | {f"PersonalVPN-ui-preview-{version}-{arch}.zip" for arch in ("x86", "x64")}
    for path in root.glob("PersonalVPN-*"):
        if path.name not in allowed or path.name not in verified:
            raise ValueError(f"Unqualified or mixed-version asset: {path.name}")
    return [{"name": name, "size": (root / name).stat().st_size, "sha256": digest(root / name)} for name in sorted(verified)]


def gh(*arguments: str, payload: dict | None = None) -> object:
    command = ["gh", *arguments]
    if payload is not None:
        command += ["--input", "-"]
    result = subprocess.run(command, input=json.dumps(payload) if payload is not None else None, text=True, capture_output=True, check=True)
    return json.loads(result.stdout) if result.stdout.strip() else None


def source_sha(repository: str, ref: str) -> str:
    item = gh("api", f"repos/{repository}/git/ref/{ref}")["object"]
    while item["type"] == "tag":
        item = gh("api", f"repos/{repository}/git/tags/{item['sha']}")["object"]
    if item["type"] != "commit":
        raise ValueError("Source ref must resolve to a commit")
    return item["sha"]


def preserve_branch(repository: str, branch: str, sha: str) -> None:
    try:
        existing = source_sha(repository, f"heads/{branch}")
    except subprocess.CalledProcessError as error:
        if "404" not in error.stderr:
            raise
        gh("api", "--method", "POST", f"repos/{repository}/git/refs", payload={"ref": f"refs/heads/{branch}", "sha": sha})
        print(f"Preserved {branch}: {sha}")
    else:
        if existing != sha:
            raise ValueError(f"Refusing to overwrite existing source branch {branch}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--assets", type=Path, required=True)
    args = parser.parse_args()
    client = Path(__file__).resolve().parents[1]
    version = (client / "VERSION").read_text().strip()
    if not re.fullmatch(r"\d+\.\d+\.\d+", version):
        raise ValueError("Invalid shared client version")
    root = args.assets.resolve()
    assets = verify_bundle(root, version)
    repository = os.environ["GITHUB_REPOSITORY"]
    sha = os.environ["GITHUB_SHA"]
    branch = os.environ["GITHUB_REF_NAME"]
    if source_sha(repository, f"heads/{branch}") != sha:
        raise ValueError("A newer source commit exists; only the current qualified commit may publish")
    # Preserve the last independently versioned clients during migration.
    for legacy in ("windows-v0.6.3", "android-v0.1.0"):
        preserve_branch(repository, f"legacy/{legacy}", source_sha(repository, f"tags/{legacy}"))
    # On subsequent releases, archive every earlier unified client source too.
    releases = gh("release", "list", "--repo", repository, "--limit", "100", "--json", "tagName,isDraft")
    for release in releases:
        tag = release["tagName"]
        if tag.startswith("client-v") and not release["isDraft"]:
            preserve_branch(repository, f"legacy/{tag}", source_sha(repository, f"tags/{tag}"))
    for prefix in ("base", "release"):
        preserve_branch(repository, f"{prefix}/client-v{version}", sha)
    for platform in ("Android", "Windows"):
        shutil.copyfile(client / platform.lower() / "README.md", root / f"INSTALL-{platform}.md")
    manifest = {"schema": 1, "version": version, "sourceCommit": sha,
                "platforms": {"windows": ["x86", "x64"], "android": ["arm64-v8a", "x86_64"]},
                "sourceBranches": [f"base/client-v{version}", f"release/client-v{version}"], "assets": assets}
    (root / "CLIENT-RELEASE.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    # Per-platform checksums were consumed above. Publish one unambiguous list.
    for path in root.glob("SHA256SUMS-*.txt"):
        path.unlink()
    lines = [f"{digest(path)}  {path.name}" for path in sorted(root.iterdir()) if path.is_file()]
    (root / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="ascii")
    subprocess.run(["gh", "release", "create", f"client-v{version}", "--repo", repository,
                    "--target", sha, "--latest", "--title", f"Personal VPN Client {version}",
                    "--notes-file", str(client / "RELEASE-NOTES.md"), *map(str, sorted(root.iterdir()))], check=True)


if __name__ == "__main__":
    main()
