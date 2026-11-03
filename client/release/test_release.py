import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import publish


class CompleteBundleTest(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.version = "0.7.0"
        for name in publish.required_assets(self.version):
            (self.root / name).write_bytes(name.encode())
        for arch in ("x86", "x64", "android"):
            names = [name for name in publish.required_assets(self.version) if (arch == "android" and (name.endswith(".apk") or name == "ANDROID-SIGNATURE.txt")) or (arch != "android" and (f"-{arch}.exe" in name or f"-{arch}-" in name))]
            (self.root / f"SHA256SUMS-{arch}.txt").write_text("\n".join(f"{publish.digest(self.root/name)}  {name}" for name in names))

    def test_all_platforms_are_manifested_and_checked(self):
        self.assertEqual(publish.required_assets(self.version), {item["name"] for item in publish.verify_bundle(self.root, self.version)})

    def test_missing_windows_architecture_rejects_entire_release(self):
        (self.root / "PersonalVPN-Setup-0.7.0-x86.exe").unlink()
        with self.assertRaisesRegex(ValueError, "complete"):
            publish.verify_bundle(self.root, self.version)

    def test_corrupted_installer_cannot_be_published(self):
        (self.root / "PersonalVPN-Android-0.7.0.apk").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            publish.verify_bundle(self.root, self.version)

    def test_extra_old_platform_installer_is_rejected(self):
        (self.root / "PersonalVPN-Android-0.1.0.apk").write_bytes(b"old")
        with self.assertRaisesRegex(ValueError, "mixed-version"):
            publish.verify_bundle(self.root, self.version)

    @patch("publish.source_sha", return_value="old-qualified-commit")
    @patch("publish.gh")
    def test_snapshot_branch_never_moves_to_new_code(self, api, resolve):
        with self.assertRaisesRegex(ValueError, "overwrite"):
            publish.preserve_branch("owner/repo", "base/client-v0.7.0", "different-commit")
        api.assert_not_called()


if __name__ == "__main__":
    unittest.main()
