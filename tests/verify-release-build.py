import importlib.util
import json
import hashlib
import io
import tarfile
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


MODULE_PATH = Path(__file__).resolve().parents[1] / "tools/build-release.py"
SPEC = importlib.util.spec_from_file_location("build_release", MODULE_PATH)
build_release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(build_release)


class ReleaseBuildTests(unittest.TestCase):
    def test_candidate_targets_the_accepted_personal_ghcr_namespace(self):
        self.assertEqual("ghcr.io/sewieland/shutterspool", build_release.IMAGE_REPOSITORY)

    def test_requires_matching_explicit_prerelease_revision(self):
        with self.assertRaises(build_release.BuildError):
            build_release.validate_selection("0.1.0-alpha.1", "a" * 40, "b" * 40, False, False)
        with self.assertRaises(build_release.BuildError):
            build_release.validate_selection("0.1.0", "a" * 40, "a" * 40, False, False)
        with self.assertRaises(build_release.BuildError):
            build_release.validate_selection("0.1.0-alpha.01", "a" * 40, "a" * 40, False, False)
        with self.assertRaises(build_release.BuildError):
            build_release.validate_selection("0.1.0-alpha.1+build.1", "a" * 40, "a" * 40, False, False)

    def test_package_declares_the_initial_migration_and_no_historical_upgrade(self):
        compatibility = build_release.DATABASE_COMPATIBILITY
        migration = build_release.ROOT / "src/ShutterSpool.Infrastructure/Persistence/Migrations" / (compatibility["initialMigration"] + ".cs")
        self.assertTrue(migration.is_file())
        self.assertEqual("initial-create", compatibility["baseline"])
        self.assertFalse(compatibility["historicalUpgradeSupported"])
        self.assertEqual("pending", compatibility["qualification"])
        self.assertEqual({"kind": "fresh-install-only", "publishedRelease": None, "qualification": "not-supported"},
                         build_release.UPGRADE_BASELINE)

    def test_dirty_source_requires_rehearsal_override(self):
        with self.assertRaises(build_release.BuildError):
            build_release.validate_selection("0.1.0-alpha.1", "a" * 40, "a" * 40, True, False)
        build_release.validate_selection("0.1.0-alpha.1", "a" * 40, "a" * 40, True, True)

    def test_rejects_lfs_pointer(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            pointer = root / "theme.bin"
            pointer.write_bytes(build_release.LFS_HEADER + b"oid sha256:" + b"0" * 64 + b"\nsize 42\n")
            self.assertTrue(build_release.is_lfs_pointer(pointer))
            pointer.write_bytes(b"hydrated theme bytes")
            self.assertFalse(build_release.is_lfs_pointer(pointer))

    def test_global_sdk_latest_patch_policy_accepts_patch_but_rejects_other_feature_bands(self):
        required = build_release.required_sdk_version()
        self.assertEqual("10.0.401", build_release.validate_sdk_version("10.0.401", required))
        for incompatible in ("10.0.399", "10.0.500", "9.0.401"):
            with self.subTest(incompatible=incompatible), self.assertRaises(build_release.BuildError):
                build_release.validate_sdk_version(incompatible, required)

    def test_candidate_requires_ci_pinned_aspire_cli_version(self):
        self.assertEqual("13.6.0+hash", build_release.validate_aspire_cli_version("13.6.0+hash"))
        with self.assertRaises(build_release.BuildError):
            build_release.validate_aspire_cli_version("13.6.1+hash")

    def test_base_manifest_resolves_build_arguments_and_rejects_unpinned_images(self):
        with tempfile.TemporaryDirectory() as temp:
            dockerfile = Path(temp) / "Dockerfile"
            dockerfile.write_text(
                "ARG SDK_VERSION=10.0.400\nARG SDK_DIGEST=sha256:synthetic\n"
                "FROM sdk:${SDK_VERSION}@${SDK_DIGEST} AS build\n"
                "FROM runtime@sha256:runtime\n", encoding="utf-8")
            self.assertEqual(["runtime@sha256:runtime", "sdk:10.0.401@sha256:synthetic"],
                             build_release.base_images(dockerfile, {"SDK_VERSION": "10.0.401"}))
            dockerfile.write_text("FROM runtime:latest\n", encoding="utf-8")
            with self.assertRaises(build_release.BuildError):
                build_release.base_images(dockerfile)

    def test_candidate_includes_the_deployment_operator_guide(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "deploy/README.md"
            destination = root / "candidate"
            source.parent.mkdir()
            source.write_text("# Standalone install guide\n", encoding="utf-8")

            packaged = build_release.copy_operator_guide(source, destination)

            self.assertEqual(destination / "README.md", packaged)
            self.assertEqual(source.read_bytes(), packaged.read_bytes())

    def test_source_fingerprint_detects_mid_build_edits(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / "Program.cs"
            source.write_text("before", encoding="utf-8")
            snapshot = build_release.source_snapshot(root, ["Program.cs"])
            before = build_release.fingerprint_snapshot(snapshot)
            source.write_text("after", encoding="utf-8")
            self.assertNotEqual(before, build_release.fingerprint_files(root, ["Program.cs"]))
            self.assertEqual(before, build_release.fingerprint_snapshot(snapshot))

    def test_manifest_hashes_match_staged_artifacts(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "docker-compose.yaml").write_text("services: {}\n", encoding="utf-8")
            artifacts = build_release.file_manifest(root)
            manifest = {"artifacts": artifacts}
            (root / "release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            for relative, expected in artifacts.items():
                path = root / relative
                self.assertEqual(expected["size"], path.stat().st_size)
                self.assertEqual(expected["sha256"], build_release.sha256_file(path))

    def test_compose_rejects_unpinned_or_source_tree_inputs(self):
        with tempfile.TemporaryDirectory() as temp:
            compose = Path(temp) / "docker-compose.yaml"
            compose.write_text("services:\n  db:\n    image: postgres:17\n", encoding="utf-8")
            with self.assertRaises(build_release.BuildError):
                build_release.validate_compose(compose)
            compose.write_text('services:\n  app:\n    volumes:\n      - "./src:/src"\n', encoding="utf-8")
            with self.assertRaises(build_release.BuildError):
                build_release.validate_compose(compose)

    def test_publish_variants_enable_only_selected_camera_edges(self):
        env = build_release.publish_environment({"ModernFtps"})
        self.assertEqual("true", env["ShutterSpool__CameraIngress__Profiles__ModernFtps__Enabled"])
        self.assertEqual("false", env["ShutterSpool__CameraIngress__Profiles__LegacyFtps__Enabled"])
        self.assertEqual("false", env["ShutterSpool__CameraIngress__Profiles__PlainFtpLan__Enabled"])

    def test_compose_rejects_unknown_generator_parameters(self):
        with tempfile.TemporaryDirectory() as temp:
            compose = Path(temp) / "docker-compose.yaml"
            compose.write_text('services:\n  app:\n    expose: ["${SERVER_PORT}"]\n', encoding="utf-8")
            with self.assertRaises(build_release.BuildError):
                build_release.validate_compose(compose)

    def test_packaged_component_and_proxy_overlays_have_pinned_images(self):
        package = MODULE_PATH.parents[1] / "deploy/package"
        overlays = [*sorted((package / "components").glob("*.yaml")),
                    *sorted((package / "proxies").glob("*.yaml"))]
        self.assertTrue(overlays)
        for overlay in overlays:
            with self.subTest(overlay=overlay.name):
                build_release.validate_compose(overlay)

    def test_manifest_docker_version_identity_requires_both_sides(self):
        self.assertEqual(("29.8.1", "29.8.1"),
                         build_release.parse_docker_versions("29.8.1|29.8.1\n"))
        with self.assertRaises(build_release.BuildError):
            build_release.parse_docker_versions("29.8.1|")

    def test_manifest_records_cli_sdk_docker_and_buildx_versions(self):
        versions = ["29.8.1|29.8.1", "10.0.401", "13.6.0+cli-hash", "buildx v0.37.1"]
        with patch.object(build_release, "run", side_effect=versions):
            recorded = build_release.build_environment(SimpleNamespace(aspire="aspire", docker="docker"))
        self.assertEqual({"aspireCli": versions[2], "dotnetSdk": versions[1],
                          "dockerClient": "29.8.1", "dockerServer": "29.8.1", "buildx": versions[3]}, recorded)

    def test_oci_requires_sbom_and_provenance_attestations(self):
        with tempfile.TemporaryDirectory() as temp:
            archive_path = Path(temp) / "candidate.oci.tar"
            blobs = {}

            def add_blob(data):
                digest = hashlib.sha256(data).hexdigest()
                blobs[digest] = data
                return "sha256:" + digest

            sbom = json.dumps({"predicateType": "https://spdx.dev/Document"}).encode()
            provenance = json.dumps({"predicateType": "https://slsa.dev/provenance/v1"}).encode()
            layers = []
            for predicate, payload in (("https://spdx.dev/Document", sbom),
                                       ("https://slsa.dev/provenance/v1", provenance)):
                layers.append({"digest": add_blob(payload), "annotations": {"in-toto.io/predicate-type": predicate}})
            attestation = {"layers": layers}
            attestation_digest = add_blob(json.dumps(attestation).encode())
            nested_digest = add_blob(json.dumps({"manifests": [
                {"digest": attestation_digest, "annotations": {"vnd.docker.reference.type": "attestation-manifest"}}
            ]}).encode())
            with tarfile.open(archive_path, "w") as bundle:
                index = json.dumps({"manifests": [{"digest": nested_digest}]}).encode()
                item = tarfile.TarInfo("index.json")
                item.size = len(index)
                bundle.addfile(item, io.BytesIO(index))
                for digest, payload in blobs.items():
                    item = tarfile.TarInfo("blobs/sha256/" + digest)
                    item.size = len(payload)
                    bundle.addfile(item, io.BytesIO(payload))
            self.assertEqual({"https://spdx.dev/Document", "https://slsa.dev/provenance/v1"},
                             set(build_release.verify_oci_attestations(archive_path, nested_digest)))


if __name__ == "__main__":
    unittest.main()
