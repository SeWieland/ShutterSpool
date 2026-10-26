import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "tools/registry-isolation.py"
SPEC = importlib.util.spec_from_file_location("registry_isolation", MODULE_PATH)
registry_isolation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(registry_isolation)


class RegistryIsolationTests(unittest.TestCase):
    def test_creates_unique_synthetic_image_and_bundle_for_the_fixed_private_package(self):
        with tempfile.TemporaryDirectory() as temp:
            result = registry_isolation.create_probe(Path(temp) / "probe", run_id="120", run_attempt="2")

            self.assertEqual("ghcr.io/sewieland/shutterspool-candidates", result["repository"])
            self.assertEqual("isolation-120-2-image", result["image_tag"])
            self.assertEqual("isolation-120-2-bundle", result["bundle_tag"])
            image = Path(result["image_layout"])
            index = json.loads((image / "index.json").read_text(encoding="utf-8"))
            self.assertEqual("application/vnd.oci.image.manifest.v1+json", index["manifests"][0]["mediaType"])
            digest = index["manifests"][0]["digest"]
            self.assertEqual(digest, result["image_digest"])
            manifest = image / "blobs" / "sha256" / digest.removeprefix("sha256:")
            self.assertEqual(digest, "sha256:" + hashlib.sha256(manifest.read_bytes()).hexdigest())

            compose = Path(result["compose_file"]).read_bytes()
            manifest_content = Path(result["manifest_file"]).read_bytes()
            self.assertEqual(hashlib.sha256(compose).hexdigest(), result["compose_sha256"])
            self.assertEqual(hashlib.sha256(manifest_content).hexdigest(), result["manifest_sha256"])
            self.assertIn(b"SYNTHETIC REGISTRY ISOLATION PROBE", compose)
            self.assertNotIn(b"shutterspool:0.", compose)

    def test_rejects_tag_components_that_could_escape_the_synthetic_namespace(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "probe"
            with self.assertRaises(ValueError):
                registry_isolation.create_probe(output, run_id="../../real", run_attempt="1")
            self.assertFalse(output.exists())

    def test_refuses_to_overwrite_probe_output(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "probe"
            registry_isolation.create_probe(output, run_id="120", run_attempt="1")

            with self.assertRaises(FileExistsError):
                registry_isolation.create_probe(output, run_id="120", run_attempt="1")

    def test_requires_explicit_access_denial_and_rejects_missing_or_transient_objects(self):
        for status, output in (
            (1, "401 Unauthorized"),
            (1, "403 Forbidden: denied"),
            (1, "Error response from registry: denied: requested access to the resource is denied"),
        ):
            with self.subTest(status=status, output=output):
                self.assertTrue(registry_isolation.is_registry_access_denial(status, output))
        for status, output in (
            (0, ""), (1, "404 Not Found"), (1, "connection timed out"),
            (1, "429 Too Many Requests"), (1, "open auth.json: permission denied"),
        ):
            with self.subTest(status=status, output=output):
                self.assertFalse(registry_isolation.is_registry_access_denial(status, output))

    def test_token_login_denial_is_unavailable_not_a_package_access_denial(self):
        self.assertEqual("available", registry_isolation.login_control_result(0, ""))
        self.assertEqual("unavailable", registry_isolation.login_control_result(1, "401 Unauthorized"))
        self.assertEqual("unavailable", registry_isolation.login_control_result(1, "denied: requested access to the resource is denied"))
        self.assertEqual("error", registry_isolation.login_control_result(1, "connection timed out"))

    def test_denial_command_distinguishes_success_from_failure_without_echoing_payload(self):
        for status, expected in ((0, "read succeeded"), (1, "exit code 1")):
            with self.subTest(status=status):
                run = subprocess.run(
                    [sys.executable, str(MODULE_PATH), "verify-denial", "--", sys.executable, "-c",
                     f"import sys; print('private-payload'); sys.exit({status})"],
                    text=True, capture_output=True, check=False,
                )
                self.assertEqual(1, run.returncode)
                self.assertIn(expected, run.stderr)
                self.assertNotIn("private-payload", run.stdout + run.stderr)

    def test_token_login_command_reports_unavailable_without_echoing_registry_output(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "github-output"
            env = os.environ.copy()
            env["GITHUB_OUTPUT"] = str(output)
            run = subprocess.run(
                [sys.executable, str(MODULE_PATH), "verify-token-login", "--", sys.executable, "-c", "import sys; print('Error response from registry: denied: requested access to the resource is denied', file=sys.stderr); sys.exit(1)"],
                env=env, text=True, capture_output=True, check=False,
            )
            self.assertEqual(0, run.returncode, run.stderr)
            self.assertEqual("available=false\n", output.read_text(encoding="utf-8"))
            self.assertNotIn("denied:", run.stdout)


if __name__ == "__main__":
    unittest.main()
