"""Check canonical image-pin parsing and refusal cases without Docker or third-party packages."""
import tempfile
import unittest
from pathlib import Path

from image_pins import load_image_pins


class ImagePinTests(unittest.TestCase):
    def test_reads_canonical_pins(self):
        pins = load_image_pins()
        self.assertEqual(set(pins), {'camera-edge', 'imaginary'})
        self.assertTrue(all('@sha256:' in pin and len(pin.rsplit('@sha256:', 1)[1]) == 64 for pin in pins.values()))

    def test_reads_future_synthetic_pin(self):
        content = ('services:\n'
                   '  camera-edge:\n'
                   '    image: registry.example/sftpgo:v99.1@sha256:' + 'c' * 64 + '\n'
                   '  imaginary:\n'
                   '    image: registry.example/imaginary:9.8.7@sha256:' + 'd' * 64 + '\n')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'compose.yaml'
            path.write_text(content, encoding='utf-8')
            pins = load_image_pins(path)
        self.assertEqual(pins['camera-edge'], 'registry.example/sftpgo:v99.1@sha256:' + 'c' * 64)
        self.assertEqual(pins['imaginary'], 'registry.example/imaginary:9.8.7@sha256:' + 'd' * 64)

    def test_rejects_missing_service_pin(self):
        self.assert_invalid('services:\n  imaginary:\n    image: h2non/imaginary:1.2.4@sha256:' + 'a' * 64 + '\n')

    def test_rejects_unpinned_image(self):
        self.assert_invalid('services:\n  camera-edge:\n    image: drakkan/sftpgo:v2.7.5\n  imaginary:\n    image: h2non/imaginary:1.2.4@sha256:' + 'a' * 64 + '\n')

    def test_rejects_duplicate_service_image(self):
        pin = 'drakkan/sftpgo:v2.7.5@sha256:' + 'a' * 64
        self.assert_invalid(f'services:\n  camera-edge:\n    image: {pin}\n  camera-edge:\n    image: {pin}\n  imaginary:\n    image: h2non/imaginary:1.2.4@sha256:' + 'b' * 64 + '\n')

    def assert_invalid(self, content):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'compose.yaml'
            path.write_text(content, encoding='utf-8')
            with self.assertRaises(ValueError):
                load_image_pins(path)


if __name__ == '__main__':
    unittest.main()
