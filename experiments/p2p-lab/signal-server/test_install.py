import unittest
from render_install import render


class Installer(unittest.TestCase):
    def test_exact_digest_and_independent_compose(self):
        image = 'ghcr.io/kittawat-thongpud/personal-vpn-server/p2p-signal@sha256:' + 'a' * 64
        script = render(image)
        self.assertEqual(script.count(image), 2)
        self.assertIn('name: personal-vpn-p2p-lab', script)
        self.assertIn('mem_limit: 64m', script)
        self.assertIn('devices.sqlite', script)
        self.assertNotIn('docker load', script)
        self.assertNotIn('@@', script)

    def test_mutable_tags_other_images_and_shell_injection_are_rejected(self):
        for image in ['ghcr.io/other/image@sha256:' + 'a' * 64,
                      'ghcr.io/kittawat-thongpud/personal-vpn-server/p2p-signal:lab.2',
                      "image'; touch /tmp/unexpected; '"]:
            with self.assertRaises(ValueError):
                render(image)
