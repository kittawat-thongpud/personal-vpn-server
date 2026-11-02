"""Verify release allowlists, deployment modes and artifact integrity."""
from __future__ import annotations

import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("release_bundle", ROOT / "scripts/release_bundle.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
DIGESTS = {"xray": "sha256:" + "a" * 64, "hysteria": "sha256:" + "b" * 64}


class ReleaseTests(unittest.TestCase):
    def test_bundle_only_contains_allowlisted_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = release.build_bundle("Example/PrivateVPN", "v0.1.0", DIGESTS, Path(temporary))
            with tarfile.open(path) as archive:
                self.assertEqual(set(archive.getnames()), set(release.FILES) |
                                 {"compose.yaml", "compose.registry.yaml", "release.json"})
                for member in archive.getmembers():
                    self.assertTrue(member.isfile())
                    self.assertNotIn("..", Path(member.name).parts)
                    self.assertFalse(member.name.startswith("/"))
                local = yaml.safe_load(archive.extractfile("compose.yaml"))
                registry = yaml.safe_load(archive.extractfile("compose.registry.yaml"))
                for name in ("xray", "hysteria"):
                    self.assertNotIn("build", local["services"][name])
                    self.assertEqual(local["services"][name]["pull_policy"], "never")
                    self.assertEqual(local["services"][name]["image"], f"personal-vpn/{name}:v0.1.0-amd64")
                    self.assertEqual(registry["services"][name]["image"],
                                     f"ghcr.io/example/privatevpn/{name}@{DIGESTS[name]}")
                    self.assertEqual(local["services"][name]["user"], "10001:10001")
                    self.assertTrue(local["services"][name]["read_only"])
                    for document in (local, registry):
                        mount = next(m for m in document["services"][name]["volumes"]
                                     if m["target"] == "/etc/vpn")
                        self.assertEqual(mount["source"], "./runtime")
                        self.assertTrue(mount["read_only"])
                        self.assertFalse(mount["bind"]["create_host_path"])
                self.assertIn("scripts/deployment.py", archive.getnames())
                self.assertEqual(archive.getmember("scripts/install-docker-ubuntu.sh").mode, 0o755)

    def test_invalid_repository_versions_and_digests_rejected(self):
        for repository, version, digests in (
            ("https://github.com/owner/repo", "v0.1.0", DIGESTS),
            ("owner/repo", "latest", DIGESTS),
            ("owner/repo", "v0.1.0; echo BAD", DIGESTS),
            ("owner/repo", "v0.1.0", {"xray": "latest"}),
        ):
            with self.subTest(repository=repository, version=version, digests=digests):
                with self.assertRaises(ValueError):
                    release.validate(repository, version, digests)

    def test_archive_checksums_cover_actual_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            path = release.build_bundle("owner/repo", "v0.1.0", DIGESTS, folder)
            (folder / "images-linux-amd64-v0.1.0.tar.gz").write_bytes(b"fake-test-image")
            checksums = release.checksums(folder).read_text().splitlines()
            self.assertEqual(len(checksums), 2)
            for line in checksums:
                digest, filename = line.split("  ")
                self.assertEqual(digest, hashlib.sha256((folder / filename).read_bytes()).hexdigest())


if __name__ == "__main__":
    unittest.main()
