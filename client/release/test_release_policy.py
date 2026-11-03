import unittest
from release_policy import runtime_changes


class PublishedVersionPolicyTest(unittest.TestCase):
    def test_ui_native_or_package_changes_require_a_new_version(self):
        paths = ["client/android/app/src/main/java/example/Screen.kt", "client/android/native/core.go", "client/windows/setup.iss", "client/Directory.Build.props"]
        self.assertEqual(paths, runtime_changes(paths))

    def test_release_classification_and_docs_keep_qualified_installers(self):
        self.assertEqual([], runtime_changes(["client/release/publish.py", "client/README.md", "client/RELEASE-NOTES.md", ".github/workflows/client-release.yml"]))

    def test_renamed_application_sources_are_still_runtime_changes(self):
        self.assertEqual(["client/windows/app/NewScreen.cs"], runtime_changes(["client/windows/app/NewScreen.cs", "client/release/test_release.py"]))
