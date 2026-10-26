import hashlib
import importlib.util
import io
import json
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "tools/ghcr-publication.py"
PROMOTION_WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/promote-container.yml"
SPEC = importlib.util.spec_from_file_location("ghcr_publication", MODULE_PATH)
ghcr_publication = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ghcr_publication)
ISOLATION_PATH = Path(__file__).resolve().parent / "../tools/registry-isolation.py"
ISOLATION_SPEC = importlib.util.spec_from_file_location("registry_isolation", ISOLATION_PATH)
registry_isolation = importlib.util.module_from_spec(ISOLATION_SPEC)
ISOLATION_SPEC.loader.exec_module(registry_isolation)


class GhcrPublicationTests(unittest.TestCase):
    def test_public_registry_versions_are_semver_prereleases_compatible_with_oci_tags(self):
        self.assertTrue(ghcr_publication.valid_public_version("0.1.0-alpha.1"))
        for invalid in ("0.1.0-alpha.01", "0.1.0", "0.1.0-alpha.1+build", "01.1.0-alpha.1"):
            with self.subTest(invalid=invalid):
                self.assertFalse(ghcr_publication.valid_public_version(invalid))

    def make_candidate(self, directory: Path):
        directory.mkdir()
        (directory / "compose.yaml").write_text("services: {}\n", encoding="utf-8")
        (directory / ".env").write_text("SERVER_IMAGE=ghcr.io/sewieland/shutterspool:0.1.0-alpha.1@sha256:" + "a" * 64 + "\n", encoding="utf-8")
        (directory / "server-image.oci.tar").write_bytes(b"synthetic OCI image archive")
        files = {path.name: {"size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                 for path in directory.iterdir()}
        manifest = {
            "format": 1,
            "version": "0.1.0-alpha.1",
            "commit": "a" * 40,
            "candidateStatus": "unreleased-candidate",
            "serverImage": {"reference": "ghcr.io/sewieland/shutterspool:0.1.0-alpha.1", "digest": "sha256:" + "a" * 64, "artifact": "server-image.oci.tar"},
            "attestations": ["https://spdx.dev/Document", "https://slsa.dev/provenance/v1"],
            "artifacts": files,
        }
        (directory / "release-manifest.json").write_text(json.dumps(manifest, sort_keys=True) + "\n", encoding="utf-8")
        return manifest

    def test_distribution_bundle_is_deterministic_and_does_not_duplicate_image_archive(self):
        with tempfile.TemporaryDirectory() as temp:
            candidate = Path(temp) / "candidate"
            self.make_candidate(candidate)
            first, second = Path(temp) / "one.tar.gz", Path(temp) / "two.tar.gz"

            identity = ghcr_publication.create_distribution_bundle(candidate, first)
            ghcr_publication.create_distribution_bundle(candidate, second)

            self.assertEqual(hashlib.sha256(first.read_bytes()).hexdigest(), hashlib.sha256(second.read_bytes()).hexdigest())
            self.assertEqual(identity["image_digest"], "sha256:" + "a" * 64)
            with tarfile.open(first, "r:gz") as packed:
                names = set(packed.getnames())
                self.assertNotIn("server-image.oci.tar", names)
                manifest_member = packed.extractfile("release-manifest.json")
                distributed_manifest = json.load(manifest_member)
                self.assertEqual(names - {"release-manifest.json"}, set(distributed_manifest["artifacts"]))
                self.assertEqual(hashlib.sha256((candidate / "server-image.oci.tar").read_bytes()).hexdigest(),
                                 distributed_manifest["serverImage"]["sourceArchiveSha256"])
                self.assertNotIn("artifact", distributed_manifest["serverImage"])
            verified = ghcr_publication.verify_distribution_bundle(first, version="0.1.0-alpha.1", commit="a" * 40, image_digest="sha256:" + "a" * 64)
            self.assertEqual(identity["bundle_sha256"], verified["bundle_sha256"])
            workflow_check = subprocess.run(
                [sys.executable, str(MODULE_PATH), "verify", "--bundle", str(first),
                 "--version", "0.1.0-alpha.1", "--commit", "a" * 40,
                 "--image-digest", "sha256:" + "a" * 64], text=True, capture_output=True, check=False,
            )
            self.assertEqual(0, workflow_check.returncode, workflow_check.stderr)
            for version, commit, image_digest in (("0.1.1-alpha.1", "a" * 40, "sha256:" + "a" * 64),
                                                  ("0.1.0-alpha.1", "b" * 40, "sha256:" + "a" * 64),
                                                  ("0.1.0-alpha.1", "a" * 40, "sha256:" + "b" * 64)):
                with self.subTest(version=version, commit=commit, digest=image_digest), self.assertRaises(ghcr_publication.PublicationError):
                    ghcr_publication.verify_distribution_bundle(first, version=version, commit=commit, image_digest=image_digest)

            with tempfile.TemporaryDirectory() as tamper_temp:
                root = Path(tamper_temp) / "contents"
                root.mkdir()
                with tarfile.open(first, "r:gz") as packed:
                    packed.extractall(root)
                env_path = root / ".env"
                env_path.write_text("SERVER_IMAGE=ghcr.io/sewieland/shutterspool:0.1.0-alpha.2@sha256:" + "b" * 64 + "\n", encoding="utf-8")
                manifest_path = root / "release-manifest.json"
                bundle_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                bundle_manifest["artifacts"][".env"] = {
                    "size": env_path.stat().st_size,
                    "sha256": hashlib.sha256(env_path.read_bytes()).hexdigest(),
                }
                manifest_path.write_text(json.dumps(bundle_manifest), encoding="utf-8")
                tampered_bundle = Path(tamper_temp) / "tampered.tar.gz"
                with tarfile.open(tampered_bundle, "w:gz") as packed:
                    for path in sorted(root.rglob("*")):
                        if path.is_file():
                            packed.add(path, arcname=path.relative_to(root).as_posix())
                with self.assertRaisesRegex(ghcr_publication.PublicationError, "canonical immutable image reference"):
                    ghcr_publication.verify_distribution_bundle(tampered_bundle, version="0.1.0-alpha.1",
                                                                commit="a" * 40, image_digest="sha256:" + "a" * 64)

    def test_creation_rejects_missing_or_duplicate_image_assignments_with_matching_inventory(self):
        for extra in ("", "SERVER_IMAGE=other:tag\n", " export SERVER_IMAGE =other:tag\n"):
            with self.subTest(extra=extra), tempfile.TemporaryDirectory() as temp:
                candidate = Path(temp) / "candidate"
                manifest = self.make_candidate(candidate)
                env_path = candidate / ".env"
                content = env_path.read_text(encoding="utf-8") + extra if extra else "# no image\n"
                env_path.write_text(content, encoding="utf-8")
                manifest["artifacts"][".env"] = {"size": env_path.stat().st_size,
                                                "sha256": hashlib.sha256(env_path.read_bytes()).hexdigest()}
                (candidate / "release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaisesRegex(ghcr_publication.PublicationError, "canonical immutable image reference"):
                    ghcr_publication.create_distribution_bundle(candidate, Path(temp) / "bundle.tar.gz")

    def test_distribution_bundle_rejects_manifest_mismatch_missing_attestation_and_path_traversal(self):
        with tempfile.TemporaryDirectory() as temp:
            candidate = Path(temp) / "candidate"
            manifest = self.make_candidate(candidate)
            (candidate / "compose.yaml").write_text("services: {changed: true}\n", encoding="utf-8")
            with self.assertRaises(ghcr_publication.PublicationError):
                ghcr_publication.create_distribution_bundle(candidate, Path(temp) / "invalid.tar.gz")

            manifest["attestations"] = ["https://spdx.dev/Document"]
            (candidate / "release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaises(ghcr_publication.PublicationError):
                ghcr_publication.create_distribution_bundle(candidate, Path(temp) / "invalid-attestation.tar.gz")

            traversal = Path(temp) / "traversal.tar.gz"
            with tarfile.open(traversal, "w:gz") as archive:
                member = tarfile.TarInfo("../escaped")
                member.size = 1
                archive.addfile(member, io.BytesIO(b"x"))
            with self.assertRaises(ghcr_publication.PublicationError):
                ghcr_publication.verify_distribution_bundle(traversal, version="0.1.0-alpha.1", commit="a" * 40, image_digest="sha256:" + "a" * 64)

    def test_tag_retry_reuses_same_digest_rejects_conflict_and_supports_partial_completion(self):
        digest = "sha256:" + "d" * 64
        self.assertEqual("publish", ghcr_publication.plan_immutable_tag(None, digest))
        self.assertEqual("reuse", ghcr_publication.plan_immutable_tag(digest, digest))
        with self.assertRaises(ghcr_publication.PublicationError):
            ghcr_publication.plan_immutable_tag("sha256:" + "e" * 64, digest)
        self.assertEqual(("reuse", "publish"), (ghcr_publication.plan_immutable_tag(digest, digest), ghcr_publication.plan_immutable_tag(None, digest)))

    def test_workflow_tag_cli_composition_handles_same_conflict_and_partial_retry(self):
        digest = "sha256:" + "f" * 64
        scenarios = (
            (0, digest + "\n", "digest=" + digest + "\n", 0),
            (1, "MANIFEST_UNKNOWN: manifest unknown", "state=missing\n", 0),
            (1, "403 Forbidden", "", 1),
            (1, "connection timed out", "", 1),
        )
        for returncode, text, expected, expected_status in scenarios:
            with self.subTest(text=text):
                run = subprocess.run(
                    [sys.executable, str(MODULE_PATH), "inspect-tag", "--", sys.executable, "-c",
                     f"import sys; print({text!r}); sys.exit({returncode})"],
                    text=True, capture_output=True, check=False,
                )
                self.assertEqual(expected_status, run.returncode)
                self.assertEqual(expected, run.stdout)

        def plan_from_inspection(returncode, output, approved_digest):
            inspection = subprocess.run(
                [sys.executable, str(MODULE_PATH), "inspect-tag", "--", sys.executable, "-c",
                 f"import sys; print({output!r}); sys.exit({returncode})"],
                text=True, capture_output=True, check=False,
            )
            if inspection.returncode != 0:
                return inspection.returncode, inspection.stderr
            run = subprocess.run([sys.executable, str(MODULE_PATH), "plan-tag", "--existing", inspection.stdout.strip(),
                                  "--approved-digest", approved_digest], text=True, capture_output=True, check=False)
            return run.returncode, run.stdout

        self.assertEqual((0, "reuse\n"), plan_from_inspection(0, digest, digest))
        self.assertEqual((0, "publish\n"), plan_from_inspection(1, "MANIFEST_UNKNOWN: manifest unknown", "sha256:" + "e" * 64))
        self.assertEqual((1, ""), plan_from_inspection(0, "sha256:" + "e" * 64, digest))
        # A partial two-object retry reuses the already-written image and plans only the missing bundle tag.
        retry_plans = (plan_from_inspection(0, digest, digest),
                       plan_from_inspection(1, "MANIFEST_UNKNOWN: manifest unknown", "sha256:" + "e" * 64))
        self.assertEqual(((0, "reuse\n"), (0, "publish\n")), retry_plans)

    def test_release_target_tags_are_disjoint_from_other_semver_image_tags(self):
        run = subprocess.run([sys.executable, str(MODULE_PATH), "target-tags", "--version", "0.1.0-alpha.1"],
                             text=True, capture_output=True, check=False)
        self.assertEqual(0, run.returncode, run.stderr)
        tags = dict(line.split("=", maxsplit=1) for line in run.stdout.splitlines())
        self.assertEqual("0.1.0-alpha.1", tags["target_image_tag"])
        self.assertEqual("bundle-0.1.0-alpha.1", tags["target_bundle_tag"])
        other_image_tags = {"0.1.0-alpha.1", "0.1.0-alpha.0"}
        self.assertNotIn(tags["target_bundle_tag"], other_image_tags)

    def test_synthetic_workflow_bundle_requires_exact_two_files_before_and_after_approval(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "bundle"
            directory.mkdir()
            compose = b"# synthetic\nservices: {}\n"
            manifest = b'{"kind":"synthetic-probe"}\n'
            (directory / "compose.yaml").write_bytes(compose)
            (directory / "manifest.json").write_bytes(manifest)
            compose_hash = hashlib.sha256(compose).hexdigest()
            manifest_hash = hashlib.sha256(manifest).hexdigest()
            oci_manifest = directory.parent / "oci-manifest.json"
            oci_manifest.write_text(json.dumps({
                "schemaVersion": 2,
                "mediaType": "application/vnd.oci.image.manifest.v1+json",
                "artifactType": "application/vnd.shutterspool.synthetic.registry-probe",
                "config": {"mediaType": "application/vnd.oci.empty.v1+json",
                           "digest": "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
                           "size": 2, "data": "e30="},
                "layers": [
                    {"mediaType": media_type, "digest": "sha256:" + file_hash,
                     "size": len(content), "annotations": {"org.opencontainers.image.title": name}}
                    for name, media_type, file_hash, content in (
                        ("compose.yaml", "application/yaml", compose_hash, compose),
                        ("manifest.json", "application/json", manifest_hash, manifest),
                    )
                ],
            }), encoding="utf-8")
            command = [sys.executable, str(MODULE_PATH), "verify-synthetic-bundle", "--directory", str(directory),
                       "--oci-manifest", str(oci_manifest),
                       "--compose-sha256", compose_hash, "--manifest-sha256", manifest_hash]
            before_approval = subprocess.run(command, text=True, capture_output=True, check=False)
            self.assertEqual(0, before_approval.returncode, before_approval.stderr)
            (directory / "unexpected.json").write_text("{}", encoding="utf-8")
            after_approval = subprocess.run(command, text=True, capture_output=True, check=False)
            self.assertEqual(1, after_approval.returncode)
            self.assertIn("unexpected files", after_approval.stderr)

            (directory / "unexpected.json").unlink()
            (directory / "compose.yaml").write_bytes(compose)
            artifact = json.loads(oci_manifest.read_text(encoding="utf-8"))
            artifact["layers"].append(dict(artifact["layers"][0]))
            oci_manifest.write_text(json.dumps(artifact), encoding="utf-8")
            extra_oci_layer = subprocess.run(command, text=True, capture_output=True, check=False)
            self.assertEqual(1, extra_oci_layer.returncode)
            self.assertIn("unexpected layers", extra_oci_layer.stderr)

    def test_promotion_workflow_routes_disjoint_tags_and_checks_visibility(self):
        workflow = PROMOTION_WORKFLOW.read_text(encoding="utf-8")
        release_publish = workflow.split("- name: Promote image and unchanged bundle without overwriting a version\n", 1)[1].split("- name: Confirm release package visibility is public\n", 1)[0]
        first_write = release_publish.index('if [[ "$image_plan" == publish ]]; then oras cp')
        self.assertLess(release_publish.index("existing_image=\"$(python tools/ghcr-publication.py inspect-tag"), first_write)
        self.assertLess(release_publish.index("existing_bundle=\"$(python tools/ghcr-publication.py inspect-tag"), first_write)
        self.assertLess(release_publish.index("image_plan=\"$(python tools/ghcr-publication.py plan-tag"), first_write)
        self.assertLess(release_publish.index("bundle_plan=\"$(python tools/ghcr-publication.py plan-tag"), first_write)
        self.assertIn("target-tags --version \"$BUILD_VERSION\" >> \"$GITHUB_OUTPUT\"", workflow)
        self.assertIn('python tools/ghcr-publication.py target-tags --version "$BUILD_VERSION" >> "$GITHUB_OUTPUT"', workflow)
        self.assertIn('TARGET_BUNDLE_TAG: ${{ needs.resolve-candidate.outputs.target_bundle_tag }}', workflow)
        self.assertIn('TARGET_BUNDLE_TAG: ${{ needs.resolve-synthetic-probe.outputs.target_bundle_tag }}', workflow)
        self.assertIn('oras cp "$CANDIDATE_PACKAGE@$BUNDLE_DIGEST" "$TARGET_PACKAGE:$TARGET_BUNDLE_TAG"', workflow)
        self.assertIn('oras cp "$CANDIDATE_PACKAGE@$IMAGE_DIGEST" "$TARGET_PACKAGE:$TARGET_IMAGE_TAG"', workflow)
        self.assertIn('gh api /user/packages/container/shutterspool --jq .visibility', workflow)
        self.assertIn('gh api /user/packages/container/shutterspool-publish-test --jq .visibility', workflow)
        self.assertNotIn("--recursive", workflow)
        self.assertEqual(3, workflow.count("verify-synthetic-bundle --directory"))
        self.assertEqual(3, workflow.count("oras manifest fetch --output \"$BUNDLE_MANIFEST\""))
        self.assertEqual(3, workflow.count("--oci-manifest \"$BUNDLE_MANIFEST\""))

    def test_registry_denials_are_distinguished_from_missing_or_transient_objects(self):
        self.assertTrue(ghcr_publication.is_registry_denial(1, "Error response from registry: denied: requested access to the resource is denied"))
        for output in ("404 Not Found", "connection timed out", "429 Too Many Requests", "open auth.json: permission denied"):
            with self.subTest(output=output):
                self.assertFalse(ghcr_publication.is_registry_denial(1, output))
        self.assertEqual(("missing", None), ghcr_publication.classify_registry_lookup(1, "404 Not Found"))
        self.assertEqual(("denied", None), ghcr_publication.classify_registry_lookup(1, "403 Forbidden"))
        digest = "sha256:" + "f" * 64
        self.assertEqual(("present", digest), ghcr_publication.classify_registry_lookup(0, digest + "\n"))
        self.assertEqual(("unverified", None), ghcr_publication.classify_registry_lookup(1, "timeout"))

    def test_registry_pulled_oci_layout_checks_root_and_every_referenced_blob(self):
        with tempfile.TemporaryDirectory() as temp:
            result = registry_isolation.create_probe(Path(temp) / "fixture", run_id="55", run_attempt="1")
            layout = Path(result["image_layout"])
            self.assertEqual([], ghcr_publication.verify_oci_layout(layout, expected_digest=result["image_digest"]))
            workflow_check = subprocess.run(
                [sys.executable, str(MODULE_PATH), "verify-image", "--layout", str(layout),
                 "--image-digest", result["image_digest"]], text=True, capture_output=True, check=False,
            )
            self.assertEqual(0, workflow_check.returncode, workflow_check.stderr)
            blob = layout / "blobs" / "sha256" / result["image_digest"].removeprefix("sha256:")
            blob.write_bytes(blob.read_bytes() + b"corrupt")
            with self.assertRaises(ghcr_publication.PublicationError):
                ghcr_publication.verify_oci_layout(layout, expected_digest=result["image_digest"])

    def test_registry_pulled_release_image_requires_real_sbom_and_provenance_predicates(self):
        with tempfile.TemporaryDirectory() as temp:
            layout = Path(temp)
            blobs = layout / "blobs" / "sha256"
            blobs.mkdir(parents=True)

            def descriptor(content, media_type, annotations=None):
                digest = hashlib.sha256(content).hexdigest()
                (blobs / digest).write_bytes(content)
                result = {"mediaType": media_type, "digest": "sha256:" + digest, "size": len(content)}
                if annotations:
                    result["annotations"] = annotations
                return result

            config = descriptor(b"{}", "application/vnd.oci.image.config.v1+json")
            image = descriptor(json.dumps({"schemaVersion": 2, "config": config, "layers": []}).encode(),
                               "application/vnd.oci.image.manifest.v1+json")
            layers = []
            for predicate in sorted(ghcr_publication.REQUIRED_ATTESTATIONS):
                statement = json.dumps({"predicateType": predicate}).encode()
                layers.append(descriptor(statement, "application/vnd.in-toto+json",
                                         {"in-toto.io/predicate-type": predicate}))
            attestation = descriptor(json.dumps({"schemaVersion": 2, "layers": layers}).encode(),
                                     "application/vnd.oci.image.manifest.v1+json",
                                     {"vnd.docker.reference.type": "attestation-manifest"})
            index_bytes = json.dumps({"schemaVersion": 2, "manifests": [image, attestation]}).encode()
            root = descriptor(index_bytes, "application/vnd.oci.image.index.v1+json")
            (layout / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}\n', encoding="utf-8")
            (layout / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [root]}), encoding="utf-8")

            self.assertEqual(sorted(ghcr_publication.REQUIRED_ATTESTATIONS),
                             ghcr_publication.verify_oci_layout(layout, expected_digest=root["digest"], require_attestations=True))
            missing = blobs / layers[-1]["digest"].removeprefix("sha256:")
            missing.unlink()
            with self.assertRaises(ghcr_publication.PublicationError):
                ghcr_publication.verify_oci_layout(layout, expected_digest=root["digest"], require_attestations=True)


if __name__ == "__main__":
    unittest.main()
