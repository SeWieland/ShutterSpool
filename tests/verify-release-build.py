import hashlib
import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("build_release", Path(__file__).resolve().parents[1] / "tools/build-release.py")
build_release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_release)
COMMIT = "a" * 40
DIGEST = "sha256:" + "b" * 64


class ReleasePackageTests(unittest.TestCase):
    def test_versions_classify_experimental_and_stable_releases(self):
        for version, experimental in (("0.1.0", True), ("0.1.0-alpha.1", True),
                                      ("1.0.0-rc.1", True), ("1.0.0", False), ("2.3.4", False)):
            with self.subTest(version=version):
                self.assertEqual(experimental, build_release.is_prerelease(version))
        for version in ("01.0.0", "1.0", "1.0.0-rc.01", "1.0.0+build.1", "v1.0.0", "1.0.0-" + "a" * 128):
            with self.subTest(version=version), self.assertRaises(build_release.BuildError):
                build_release.validate_version(version)

    def args(self, output, **changes):
        return SimpleNamespace(**({"version": "0.1.0", "revision": COMMIT, "image_digest": DIGEST,
                                   "output": output, "aspire": "aspire"} | changes))

    def generate_compose(self, command, *, env=None, **kwargs):
        if command == ["git", "rev-parse", "HEAD"]:
            return COMMIT
        self.assertEqual(["aspire", "publish"], command[:2])
        output = Path(command[command.index("--output-path") + 1])
        services = ["  server:\n    image: '${SERVER_IMAGE:?Select the qualified Server image}'"]
        for profile, service in (("ModernFtps", "camera-edge"), ("LegacyFtps", "camera-edge-legacy"),
                                 ("PlainFtpLan", "camera-edge-plain")):
            if env[f"ShutterSpool__CameraIngress__Profiles__{profile}__Enabled"] == "true":
                services.append(f"  {service}:\n    image: example/edge@{DIGEST}")
        (output / "docker-compose.yaml").write_text("services:\n" + "\n".join(services) + "\n", encoding="utf-8")
        return ""

    def test_package_contains_profiles_support_files_inventory_and_pinned_image(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(build_release, "run", side_effect=self.generate_compose):
            root = build_release.prepare(self.args(Path(temp) / "release"))
            archive = root / "shutterspool-compose.zip"
            with zipfile.ZipFile(archive) as bundle:
                files = set(bundle.namelist())
                manifest = json.loads(bundle.read("release-manifest.json"))
                self.assertEqual(files, set(manifest["files"]))
                self.assertTrue({".env", ".env.example", "compose.yaml", "profiles/all-camera-edges.yaml",
                                 "README.md", "package/edge-entrypoint.sh", "package/identity-entrypoint.sh",
                                 "package/postgres-init.sh", "certificates/README.md", "operations/README.md"} <= files)
                for directory in ("config/", "package/realms/", "package/theme/", "package/traefik/", "components/", "proxies/"):
                    self.assertTrue(any(name.startswith(directory) for name in files), directory)
                for notice in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
                    self.assertEqual((build_release.ROOT / notice).read_bytes(), bundle.read(notice))
                self.assertEqual(
                    (build_release.ROOT / "src/ShutterSpool.AppHost/Themes/shutterspool/login/resources/fonts/Manrope-OFL.txt").read_bytes(),
                    bundle.read("package/theme/login/resources/fonts/Manrope-OFL.txt"))
                env = bundle.read(".env").decode()
                self.assertIn(f"SERVER_IMAGE=ghcr.io/sewieland/shutterspool:0.1.0@{DIGEST}", env)
                self.assertIn("SHUTTERSPOOL_VERSION=0.1.0", env)
                self.assertNotIn("REPLACE_WITH_IMAGE_DIGEST", env)
                core = bundle.read("compose.yaml").decode()
                all_edges = bundle.read("profiles/all-camera-edges.yaml").decode()
                self.assertNotIn("camera-edge-plain:", core)
                self.assertIn("camera-edge-plain:", all_edges)
                self.assertIn("camera-edge-legacy:", all_edges)
                self.assertEqual(COMMIT, manifest["commit"])
                self.assertEqual(DIGEST, manifest["serverImage"]["digest"])
                self.assertIn(f"example/edge@{DIGEST}", manifest["upstreamImages"])
                self.assertFalse(manifest["databaseCompatibility"]["historicalUpgradeSupported"])
                self.assertEqual({"kind": "fresh-install-only", "publishedRelease": None}, manifest["upgradeBaseline"])
            self.assertEqual(f"{hashlib.sha256(archive.read_bytes()).hexdigest()}  {archive.name}\n",
                             archive.with_suffix(".zip.sha256").read_text())

    def test_invalid_identity_and_existing_output_are_refused(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(build_release, "run", return_value=COMMIT):
            root = Path(temp) / "release"
            for change in ({"revision": "b" * 40}, {"revision": "main"}, {"image_digest": "latest"}):
                with self.subTest(change=change), self.assertRaises(build_release.BuildError):
                    build_release.prepare(self.args(root, **change))
                self.assertFalse(root.exists())
            root.mkdir()
            marker = root / "preserve.txt"
            marker.write_text("keep")
            with self.assertRaises(FileExistsError):
                build_release.prepare(self.args(root))
            self.assertEqual("keep", marker.read_text())

    def test_generation_failure_leaves_inspectable_output_without_archive(self):
        with tempfile.TemporaryDirectory() as temp, patch.object(build_release, "run", return_value=COMMIT), \
                patch.object(build_release, "publish_compose", side_effect=build_release.BuildError("failed")):
            root = Path(temp) / "release"
            with self.assertRaises(build_release.BuildError):
                build_release.prepare(self.args(root))
            self.assertTrue(root.is_dir())
            self.assertFalse((root / "shutterspool-compose.zip").exists())

    def test_compose_rejects_unpinned_images_source_mounts_and_unknown_parameters(self):
        with tempfile.TemporaryDirectory() as temp:
            compose = Path(temp) / "compose.yaml"
            for invalid in ("services:\n  db:\n    image: postgres:17\n",
                            'services:\n  app:\n    volumes: ["./src:/src"]\n',
                            'services:\n  app:\n    expose: ["${SERVER_PORT}"]\n'):
                with self.subTest(compose=invalid), self.assertRaises(build_release.BuildError):
                    compose.write_text(invalid, encoding="utf-8")
                    build_release.validate_compose(compose)


if __name__ == "__main__":
    unittest.main()
