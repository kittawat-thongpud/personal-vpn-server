"""Fail closed when Compose points at another deployment or hides the bind mount."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from scripts import deployment


class DeploymentTests(unittest.TestCase):
    def test_wrong_or_writable_mount_prevents_any_container_start(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            (root / "compose.yaml").touch()
            mount = {"type": "bind", "source": str(root / "runtime"),
                     "target": "/etc/vpn", "read_only": True}
            valid = {"services": {name: {"user": "10001:10001", "volumes": [copy.deepcopy(mount)]}
                                   for name in ("xray", "hysteria")}}
            for name in ("xray", "hysteria"):
                for change in ({"source": str(root / "other-runtime")},
                               {"read_only": False}, {"type": "volume"}):
                    with self.subTest(service=name, change=change):
                        document = copy.deepcopy(valid)
                        document["services"][name]["volumes"][0].update(change)
                        with patch.object(deployment, "run", return_value=json.dumps(document)) as run:
                            with self.assertRaises(ValueError):
                                deployment.start(root)
                            # Only Compose normalization ran; image/container actions never ran.
                            self.assertEqual(run.call_count, 1)


if __name__ == "__main__":
    unittest.main()
