import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import publish


class CompleteBundleTest(unittest.TestCase):
    version = "0.7.0"

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        for name in publish.required_assets(self.version):
            (self.root / name).write_bytes(name.encode())
        for arch in ("x86", "x64", "android"):
            names = [name for name in publish.required_assets(self.version)
                     if (arch == "android" and ("Android-" in name or name == "ANDROID-SIGNATURE.txt"))
                     or (arch != "android" and (f"-{arch}.exe" in name or f"-{arch}-" in name))]
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


class DevelopmentBundleTest(unittest.TestCase):
    def test_qualification_requires_checksummed_android_preview(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            version = "0.9.7-dev.1"
            preview = f"PersonalVPN-Android-ui-preview-{version}.zip"
            assets = publish.required_assets(version)
            self.assertIn(preview, assets)
            self.assertNotIn("PersonalVPN-Android-ui-preview-0.9.7.zip", publish.required_assets("0.9.7"))
            for name in assets - {preview}:
                (root / name).write_bytes(name.encode())
            with self.assertRaisesRegex(ValueError, "complete"):
                publish.verify_bundle(root, version)
            (root / preview).write_bytes(b"emulator screenshots")
            for arch in ("x86", "x64", "android"):
                names = [name for name in assets - {preview}
                         if (arch == "android" and ("Android-" in name or name == "ANDROID-SIGNATURE.txt"))
                         or (arch != "android" and (f"-{arch}.exe" in name or f"-{arch}-" in name))]
                (root / f"SHA256SUMS-{arch}.txt").write_text(
                    "\n".join(f"{publish.digest(root / name)}  {name}" for name in names) + "\n")
            with self.assertRaisesRegex(ValueError, "platform checksum"):
                publish.verify_bundle(root, version)
            with (root / "SHA256SUMS-android.txt").open("a") as checksums:
                checksums.write(f"{publish.digest(root / preview)}  {preview}\n")
            self.assertEqual(assets, {item["name"] for item in publish.verify_bundle(root, version)})


class StableReleaseHistoryTest(unittest.TestCase):
    @patch("publish.preserve_branch")
    @patch("publish.source_sha", return_value="qualified-commit")
    @patch("publish.gh")
    def test_stable_history_skips_prereleases_drafts_and_server_releases(self, api, resolve, preserve):
        api.return_value = [
            {"tagName": "client-v0.9.5", "isDraft": False, "isPrerelease": False},
            {"tagName": "client-v0.10.0-alpha.1", "isDraft": False, "isPrerelease": True},
            {"tagName": "client-v0.9.6", "isDraft": True, "isPrerelease": False},
            {"tagName": "server-v0.1.0", "isDraft": False, "isPrerelease": False},
        ]
        publish.preserve_previous_clients("owner/repo")
        self.assertEqual([args[1] for args, _ in preserve.call_args_list],
                         ["legacy/windows-v0.6.3", "legacy/android-v0.1.0", "legacy/client-v0.9.5"])
        self.assertEqual([args[1] for args, _ in resolve.call_args_list],
                         ["tags/windows-v0.6.3", "tags/android-v0.1.0", "tags/client-v0.9.5"])


if __name__ == "__main__":
    unittest.main()
