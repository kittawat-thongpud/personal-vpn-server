"""Control-plane security and lifecycle checks; no network or Docker required."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.parse import parse_qs, urlparse

from cryptography import x509
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec


PROJECT = Path(__file__).resolve().parents[1]


@unittest.skipIf(os.name == "posix" and os.geteuid() != 0,
                 "vpnctl requires root to assign runtime GID 10001 on Linux")
class ControlPlaneTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="vpnctl-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.init()

    def cli(self, *arguments, ok=True):
        result = subprocess.run(
            [sys.executable, str(PROJECT / "vpnctl.py"), "--root", str(self.root), *arguments],
            capture_output=True, text=True, timeout=20,
        )
        if ok:
            self.assertEqual(result.returncode, 0, result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, "Command unexpectedly succeeded")
        return result

    def init(self, *extra, ok=True):
        return self.cli("init", "--endpoint", "203.0.113.10", "--reality-target",
                        "example.com:443", "--reality-sni", "example.com", *extra, ok=ok)

    def read(self, path):
        return json.loads((self.root / path).read_text(encoding="utf-8"))

    def test_devices_have_independent_credentials_and_revocation_is_complete(self):
        self.cli("add-device", "phone")
        self.cli("add-device", "laptop")
        before = self.read(".state/server.json")
        phone, laptop = before["devices"]["phone"], before["devices"]["laptop"]
        for key in ("uuid", "short_id", "hy2_password"):
            self.assertNotEqual(phone[key], laptop[key])
        self.cli("revoke-device", "phone")
        after = self.read(".state/server.json")
        self.assertFalse(after["devices"]["phone"]["active"])
        self.assertEqual(after["devices"]["laptop"], laptop)
        inbound = self.read("runtime/xray.json")["inbounds"][0]
        self.assertEqual([c["id"] for c in inbound["settings"]["users"]], [laptop["uuid"]])
        self.assertEqual(inbound["streamSettings"]["realitySettings"]["shortIds"], [laptop["short_id"]])
        self.assertEqual(self.read("runtime/hysteria.json")["auth"]["userpass"],
                         {"laptop": laptop["hy2_password"]})
        for filename in ("xray.json", "hysteria.json", "links.txt"):
            self.assertFalse((self.root / "exports/phone" / filename).exists())
        self.cli("export", "phone", ok=False)
        self.cli("add-device", "phone", ok=False)

    def test_reinit_and_duplicate_add_preserve_existing_secrets(self):
        self.cli("add-device", "phone")
        state = (self.root / ".state/server.json").read_bytes()
        cert = (self.root / "runtime/tls.crt").read_bytes()
        self.init(ok=False)
        self.cli("add-device", "phone", ok=False)
        self.assertEqual((self.root / ".state/server.json").read_bytes(), state)
        self.assertEqual((self.root / "runtime/tls.crt").read_bytes(), cert)

    def test_terminal_output_does_not_disclose_credentials(self):
        outputs = self.cli("add-device", "phone").stdout
        outputs += self.cli("list-devices").stdout
        outputs += self.cli("export", "phone").stdout
        outputs += self.cli("doctor").stdout
        state = self.read(".state/server.json")
        values = [state["reality_private_key"], state["obfs_password"],
                  *[state["devices"]["phone"][k] for k in ("uuid", "short_id", "hy2_password")]]
        for secret in values:
            self.assertNotIn(secret, outputs)

    def test_untrusted_device_names_cannot_write_outside_exports(self):
        original = (self.root / ".state/server.json").read_bytes()
        for name in ("../escape", "a/b", "a\\b", "a:b", ".", "UPPER", "a" * 33):
            self.cli("add-device", name, ok=False)
        self.assertEqual((self.root / ".state/server.json").read_bytes(), original)

    def test_pinned_tls_exports_exact_certificate_fingerprint(self):
        self.cli("add-device", "phone")
        cert = x509.load_pem_x509_certificate((self.root / "runtime/tls.crt").read_bytes())
        expected = cert.fingerprint(hashes.SHA256()).hex()
        client = self.read("exports/phone/hysteria.json")
        self.assertTrue(client["tls"]["insecure"])
        self.assertEqual(client["tls"]["pinSHA256"], expected)
        self.assertIsInstance(cert.public_key(), ec.EllipticCurvePublicKey)
        links = (self.root / "exports/phone/links.txt").read_text().splitlines()
        self.assertEqual(len(links), 2)
        parsed = urlparse(links[1])
        self.assertEqual(parse_qs(parsed.query)["pinSHA256"], [expected])
        self.assertEqual(parse_qs(parsed.query)["insecure"], ["1"])

    def test_certificate_rotation_updates_profiles_but_preserves_authentication(self):
        self.cli("add-device", "phone")
        old = self.read("exports/phone/hysteria.json")
        state = self.read(".state/server.json")
        self.cli("rotate-certificate")
        new = self.read("exports/phone/hysteria.json")
        self.assertNotEqual(old["tls"]["pinSHA256"], new["tls"]["pinSHA256"])
        self.assertEqual(old["auth"], new["auth"])
        self.assertEqual(self.read(".state/server.json"), state)

    def test_acme_uses_http_challenge_without_claiming_tcp443(self):
        self.cli("configure", "--hy2-mode", "acme", "--hy2-domain", "vpn.example.com",
                 "--acme-email", "owner@example.com")
        self.cli("add-device", "phone")
        server = self.read("runtime/hysteria.json")
        self.assertEqual(server["acme"]["type"], "http")
        self.assertNotIn("tls", server)
        self.assertNotIn("listenHTTPS", server["masquerade"])
        client = self.read("exports/phone/hysteria.json")
        self.assertFalse(client["tls"]["insecure"])
        self.assertNotIn("pinSHA256", client["tls"])

    def test_invalid_settings_fail_before_state_change(self):
        original = (self.root / ".state/server.json").read_bytes()
        for arguments in (("--endpoint", "https://example.com/path"),
                          ("--reality-target", "example.com"),
                          ("--reality-port", "65536"),
                          ("--hy2-mode", "acme"),
                          ("--bind", "not-an-ip")):
            self.cli("configure", *arguments, ok=False)
            self.assertEqual((self.root / ".state/server.json").read_bytes(), original)

    def test_concurrency_defaults_do_not_force_mux_or_brutal(self):
        self.cli("add-device", "phone")
        server = self.read("runtime/hysteria.json")
        self.assertEqual(server["congestion"]["type"], "bbr")
        self.assertTrue(server["ignoreClientBandwidth"])
        self.assertNotIn("bandwidth", server)
        self.assertFalse(server["disableUDP"])
        client = self.read("exports/phone/xray.json")
        self.assertFalse(client["outbounds"][0]["mux"]["enabled"])
        self.assertEqual(client["inbounds"][0]["listen"], "127.0.0.1")
        self.assertTrue(any("127.0.0.0/8" in rule for rule in server["acl"]["inline"]))

    def test_salamander_is_opt_in_and_exported_consistently(self):
        self.cli("add-device", "phone")
        self.assertNotIn("obfs", self.read("runtime/hysteria.json"))
        self.cli("configure", "--obfs", "salamander")
        server = self.read("runtime/hysteria.json")
        client = self.read("exports/phone/hysteria.json")
        self.assertEqual(server["obfs"], client["obfs"])

    def test_empty_device_set_has_valid_unexported_auth_sentinel(self):
        initial = self.read("runtime/hysteria.json")["auth"]["userpass"]
        self.assertEqual(list(initial), ["_no_devices_"])
        self.assertGreaterEqual(len(initial["_no_devices_"]), 32)
        self.cli("add-device", "phone")
        self.cli("revoke-device", "phone")
        auth = self.read("runtime/hysteria.json")["auth"]["userpass"]
        self.assertEqual(list(auth), ["_no_devices_"])
        self.assertNotEqual(initial["_no_devices_"], auth["_no_devices_"])
        self.assertEqual(self.read("runtime/xray.json")["inbounds"][0]["settings"]["users"], [])
        self.assertFalse((self.root / "exports/_no_devices_").exists())

    def test_ipv4_mapped_cidr_does_not_break_or_block_all_ipv4(self):
        # Go IP parsers can normalize mapped IPv6 into IPv4, making this /96
        # invalid for Xray and equivalent to all IPv4 for some matchers.
        xray = self.read("runtime/xray.json")
        hy2 = self.read("runtime/hysteria.json")
        self.assertNotIn("::ffff:0:0/96", json.dumps(xray))
        self.assertNotIn("::ffff:0:0/96", json.dumps(hy2))

    def test_missing_runtime_config_is_recoverable_without_changing_credentials(self):
        self.cli("add-device", "phone")
        state = (self.root / ".state/server.json").read_bytes()
        certificate = (self.root / "runtime/tls.crt").read_bytes()
        config = self.root / "runtime/xray.json"
        expected = config.read_bytes()
        config.unlink()
        result = self.cli("start", ok=False)
        self.assertIn(str(config.resolve()), result.stderr)
        self.cli("render")
        self.cli("doctor")
        self.assertEqual(config.read_bytes(), expected)
        self.assertEqual((self.root / ".state/server.json").read_bytes(), state)
        self.assertEqual((self.root / "runtime/tls.crt").read_bytes(), certificate)

    @unittest.skipUnless(os.name == "posix", "POSIX container group permissions")
    def test_doctor_rejects_runtime_files_unreadable_by_container_gid(self):
        config = self.root / "runtime/xray.json"
        config.chmod(0o600)
        self.assertIn("cannot read", self.cli("doctor", ok=False).stderr)
        self.cli("render")
        os.chown(config, -1, 0)
        self.assertIn("cannot read", self.cli("doctor", ok=False).stderr)
        self.cli("render")
        self.cli("doctor")


if __name__ == "__main__":
    unittest.main()
