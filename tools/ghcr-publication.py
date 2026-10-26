#!/usr/bin/env python3
"""Validate and package immutable GHCR candidate artifacts for promotion."""

from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path, PurePosixPath


IMAGE_REPOSITORY = "ghcr.io/sewieland/shutterspool"
REQUIRED_ATTESTATIONS = {"https://spdx.dev/Document", "https://slsa.dev/provenance/v1"}
PUBLIC_VERSION_PATTERN = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)\Z")


class PublicationError(RuntimeError):
    pass


def valid_public_version(version: str) -> bool:
    match = PUBLIC_VERSION_PATTERN.fullmatch(version)
    return bool(match) and not any(identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0")
                                   for identifier in match.group(4).split("."))


def public_version_tags(version: str) -> dict[str, str]:
    if not valid_public_version(version):
        raise PublicationError("Release tag requires a valid OCI-compatible SemVer prerelease.")
    return {"target_image_tag": version, "target_bundle_tag": f"bundle-{version}"}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _safe_relative(name: str) -> PurePosixPath:
    path = PurePosixPath(name)
    if path.is_absolute() or not path.parts or any(part in {"", ".", ".."} for part in path.parts):
        raise PublicationError(f"Unsafe artifact path in candidate: {name}")
    return path


def _verify_canonical_environment(directory: Path, version: str, digest: str) -> None:
    try:
        lines = (directory / ".env").read_text(encoding="utf-8").splitlines()
    except OSError as exc:
        raise PublicationError("Candidate is missing its canonical immutable image reference.") from exc
    assignments = [line for line in lines if re.match(r"^\s*(?:export\s+)?SERVER_IMAGE\s*=", line)]
    if assignments != [f"SERVER_IMAGE={IMAGE_REPOSITORY}:{version}@{digest}"]:
        raise PublicationError("Candidate must contain exactly one canonical immutable image reference.")


def _read_candidate(directory: Path) -> tuple[dict, Path]:
    manifest_path = directory / "release-manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PublicationError("Candidate release manifest is missing or invalid.") from exc
    if manifest.get("format") != 1 or manifest.get("candidateStatus") != "unreleased-candidate":
        raise PublicationError("Only a clean, committed unreleased candidate can be staged.")
    version = manifest.get("version", "")
    if not valid_public_version(version):
        raise PublicationError("Candidate manifest does not contain an explicit SemVer prerelease version.")
    commit = manifest.get("commit", "")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise PublicationError("Candidate manifest does not contain a full source commit SHA.")
    image = manifest.get("serverImage", {})
    digest = image.get("digest", "")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
        raise PublicationError("Candidate manifest does not contain a valid image digest.")
    if image.get("reference") != f"{IMAGE_REPOSITORY}:{version}" or image.get("artifact") != "server-image.oci.tar":
        raise PublicationError("Candidate image reference or OCI archive does not match the official package contract.")
    attestations = set(manifest.get("attestations", []))
    if not REQUIRED_ATTESTATIONS.issubset(attestations):
        raise PublicationError("Candidate is missing the required SBOM or provenance attestation.")
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, dict) or not artifacts:
        raise PublicationError("Candidate manifest has no artifact file inventory.")
    for relative, identity in artifacts.items():
        relpath = _safe_relative(relative)
        path = directory.joinpath(*relpath.parts)
        if not path.is_file() or path.is_symlink():
            raise PublicationError(f"Candidate artifact is missing or unsafe: {relative}")
        if path.stat().st_size != identity.get("size") or sha256_file(path) != identity.get("sha256"):
            raise PublicationError(f"Candidate artifact hash does not match its manifest: {relative}")
    image_archive = image["artifact"]
    if image_archive not in artifacts:
        raise PublicationError("Candidate manifest does not inventory its OCI image archive.")
    expected_files = set(artifacts) | {"release-manifest.json"}
    actual_files = {path.relative_to(directory).as_posix() for path in directory.rglob("*") if path.is_file()}
    if actual_files != expected_files:
        raise PublicationError("Candidate file set differs from the release manifest inventory.")
    _verify_canonical_environment(directory, version, digest)
    return manifest, directory / image_archive


def create_distribution_bundle(candidate: Path, output: Path) -> dict[str, str]:
    manifest, image_archive = _read_candidate(candidate)
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise PublicationError(f"Refusing to overwrite an existing distribution bundle: {output}")
    entries = [path for path in candidate.rglob("*") if path.is_file() and path != image_archive and path.name != "release-manifest.json"]
    entries.sort(key=lambda path: path.relative_to(candidate).as_posix())
    bundle_manifest = dict(manifest)
    bundle_image = dict(manifest["serverImage"])
    bundle_image["sourceArchiveSha256"] = manifest["artifacts"][manifest["serverImage"]["artifact"]]["sha256"]
    bundle_image.pop("artifact", None)
    bundle_manifest["serverImage"] = bundle_image
    bundle_manifest["artifacts"] = {
        path.relative_to(candidate).as_posix(): {"size": path.stat().st_size, "sha256": sha256_file(path)}
        for path in entries
    }
    bundle_manifest_bytes = (json.dumps(bundle_manifest, indent=2) + "\n").encode("utf-8")
    try:
        with output.open("xb") as raw:
            with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, filename="") as compressed:
                with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
                    for path in entries:
                        relative = path.relative_to(candidate).as_posix()
                        info = archive.gettarinfo(str(path), arcname=relative)
                        info.mtime = 0
                        info.uid = info.gid = 0
                        info.uname = info.gname = ""
                        info.mode &= 0o777
                        with path.open("rb") as stream:
                            archive.addfile(info, stream)
                    manifest_info = tarfile.TarInfo("release-manifest.json")
                    manifest_info.size = len(bundle_manifest_bytes)
                    manifest_info.mtime = 0
                    manifest_info.uid = manifest_info.gid = 0
                    manifest_info.uname = manifest_info.gname = ""
                    manifest_info.mode = 0o644
                    archive.addfile(manifest_info, io.BytesIO(bundle_manifest_bytes))
    except OSError as exc:
        output.unlink(missing_ok=True)
        raise PublicationError(f"Could not create the distribution bundle: {exc}") from exc
    return {
        "version": manifest["version"],
        "commit": manifest["commit"],
        "image_digest": manifest["serverImage"]["digest"],
        "bundle_sha256": sha256_file(output),
    }


def verify_distribution_bundle(bundle: Path, *, version: str, commit: str, image_digest: str) -> dict[str, str]:
    with tempfile.TemporaryDirectory(prefix="shutterspool-bundle-check-") as temporary:
        root = Path(temporary).resolve()
        try:
            with tarfile.open(bundle, "r:gz") as archive:
                seen = set()
                for member in archive.getmembers():
                    relative = _safe_relative(member.name)
                    target = (root.joinpath(*relative.parts)).resolve()
                    if root not in target.parents:
                        raise PublicationError("Distribution bundle contains a path outside its extraction directory.")
                    if member.name in seen or not member.isfile():
                        raise PublicationError("Distribution bundle contains a duplicate or unsupported entry.")
                    seen.add(member.name)
                    source = archive.extractfile(member)
                    if source is None:
                        raise PublicationError("Distribution bundle contains an unreadable file.")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(source.read())
        except (OSError, tarfile.TarError) as exc:
            raise PublicationError("Distribution bundle is missing or malformed.") from exc
        manifest = _read_candidate_bundle(root)
        if manifest["version"] != version or manifest["commit"] != commit or manifest["serverImage"]["digest"] != image_digest:
            raise PublicationError("Distribution bundle identity differs from the approved candidate.")
        return {"version": version, "commit": commit, "image_digest": image_digest,
                "bundle_sha256": sha256_file(bundle)}


def _read_candidate_bundle(directory: Path) -> dict:
    manifest_path = directory / "release-manifest.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PublicationError("Distribution bundle is missing its candidate manifest.") from exc
    if manifest.get("candidateStatus") != "unreleased-candidate":
        raise PublicationError("Distribution bundle is not a clean unreleased candidate.")
    if not REQUIRED_ATTESTATIONS.issubset(set(manifest.get("attestations", []))):
        raise PublicationError("Distribution bundle's image manifest omits SBOM or provenance.")
    artifacts = manifest.get("artifacts", {})
    image = manifest.get("serverImage", {})
    if not isinstance(artifacts, dict) or not re.fullmatch(r"[0-9a-f]{64}", image.get("sourceArchiveSha256", "")):
        raise PublicationError("Distribution bundle manifest does not identify its separately stored image archive.")
    if "artifact" in image:
        raise PublicationError("Distribution bundle must not advertise an image archive it does not contain.")
    expected_files = set(artifacts)
    actual_files = {path.relative_to(directory).as_posix() for path in directory.rglob("*") if path.is_file() and path != manifest_path}
    if actual_files != expected_files:
        raise PublicationError("Distribution bundle file set differs from the candidate manifest.")
    for relative in expected_files:
        relpath = _safe_relative(relative)
        path = directory.joinpath(*relpath.parts)
        identity = artifacts[relative]
        if path.stat().st_size != identity.get("size") or sha256_file(path) != identity.get("sha256"):
            raise PublicationError(f"Distribution bundle file hash does not match its candidate manifest: {relative}")
    if (not valid_public_version(manifest.get("version", ""))
            or not re.fullmatch(r"[0-9a-f]{40}", manifest.get("commit", ""))
            or not re.fullmatch(r"sha256:[0-9a-f]{64}", image.get("digest", ""))):
        raise PublicationError("Distribution bundle manifest contains an invalid release identity.")
    if image.get("reference") != f"{IMAGE_REPOSITORY}:{manifest.get('version')}":
        raise PublicationError("Distribution bundle image reference is not the canonical release repository.")
    _verify_canonical_environment(directory, manifest["version"], image["digest"])
    return manifest


def verify_oci_layout(layout: Path, *, expected_digest: str, require_attestations: bool = False) -> list[str]:
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", expected_digest):
        raise PublicationError("OCI image digest is invalid.")
    try:
        index = json.loads((layout / "index.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PublicationError("Pulled OCI layout is missing a valid index.") from exc
    root_descriptor = next((item for item in index.get("manifests", []) if item.get("digest") == expected_digest), None)
    if root_descriptor is None:
        raise PublicationError("Pulled OCI layout does not contain the approved root digest.")
    pending = [root_descriptor]
    seen: set[str] = set()
    attestation_manifests: set[str] = set()
    predicates: set[str] = set()
    while pending:
        descriptor = pending.pop()
        digest = descriptor.get("digest", "")
        if digest in seen:
            continue
        match = re.fullmatch(r"sha256:([0-9a-f]{64})", digest)
        if not match:
            raise PublicationError("OCI graph contains an unsupported or malformed digest.")
        blob = layout / "blobs" / "sha256" / match.group(1)
        if not blob.is_file() or blob.is_symlink():
            raise PublicationError(f"OCI graph is missing blob {digest}.")
        if sha256_file(blob) != match.group(1) or blob.stat().st_size != descriptor.get("size"):
            raise PublicationError(f"OCI graph blob does not match its descriptor: {digest}.")
        seen.add(digest)
        annotations = descriptor.get("annotations", {})
        if annotations.get("vnd.docker.reference.type") == "attestation-manifest":
            attestation_manifests.add(digest)
        media_type = descriptor.get("mediaType", "")
        if media_type.endswith(".manifest.v1+json") or media_type.endswith(".manifest.v2+json"):
            try:
                with blob.open("r", encoding="utf-8") as stream:
                    document = json.load(stream)
            except (OSError, json.JSONDecodeError) as exc:
                raise PublicationError(f"OCI manifest is malformed: {digest}.") from exc
            children = []
            if isinstance(document.get("config"), dict):
                children.append(document["config"])
            children.extend(document.get("layers", []))
            pending.extend(children)
            if digest in attestation_manifests:
                for layer in document.get("layers", []):
                    predicate = layer.get("annotations", {}).get("in-toto.io/predicate-type")
                    if not predicate:
                        continue
                    layer_match = re.fullmatch(r"sha256:([0-9a-f]{64})", layer.get("digest", ""))
                    if layer_match is None:
                        raise PublicationError("OCI attestation contains an invalid statement digest.")
                    payload = layout / "blobs" / "sha256" / layer_match.group(1)
                    try:
                        if (not payload.is_file() or payload.is_symlink()
                                or sha256_file(payload) != layer_match.group(1)
                                or payload.stat().st_size != layer.get("size")):
                            raise PublicationError("OCI attestation statement does not match its descriptor.")
                        with payload.open("r", encoding="utf-8") as stream:
                            statement = json.load(stream)
                    except (OSError, json.JSONDecodeError) as exc:
                        raise PublicationError("OCI attestation statement is missing or invalid.") from exc
                    if statement.get("predicateType") == predicate:
                        predicates.add(predicate)
        elif media_type.endswith(".index.v1+json") or media_type.endswith(".manifest.list.v2+json"):
            try:
                with blob.open("r", encoding="utf-8") as stream:
                    document = json.load(stream)
            except (OSError, json.JSONDecodeError) as exc:
                raise PublicationError(f"OCI index is malformed: {digest}.") from exc
            pending.extend(document.get("manifests", []))
    required = REQUIRED_ATTESTATIONS if require_attestations else set()
    if required - predicates:
        raise PublicationError("Pulled OCI graph lacks required SBOM/provenance predicate payloads.")
    return sorted(predicates)


def plan_immutable_tag(existing_digest: str | None, approved_digest: str) -> str:
    if existing_digest is None:
        return "publish"
    if existing_digest == approved_digest:
        return "reuse"
    raise PublicationError("Version already identifies different content; refusing to overwrite it.")


def is_registry_denial(returncode: int, output: str) -> bool:
    pattern = r"\b(?:401|403)\b|(?:unauthorized|denied)\s*:\s*(?:authentication required|requested access to the resource is denied|insufficient_scope)"
    return returncode != 0 and bool(re.search(pattern, output, re.IGNORECASE))


def classify_registry_lookup(returncode: int, output: str) -> tuple[str, str | None]:
    if returncode == 0:
        digest = output.strip().splitlines()[-1] if output.strip() else ""
        if re.fullmatch(r"sha256:[0-9a-f]{64}", digest):
            return "present", digest
        return "unverified", None
    if is_registry_denial(returncode, output):
        return "denied", None
    if re.search(r"\b404\b|manifest unknown|MANIFEST_UNKNOWN", output, re.IGNORECASE):
        return "missing", None
    return "unverified", None


def verify_synthetic_bundle(directory: Path, *, oci_manifest: Path, compose_sha256: str, manifest_sha256: str) -> None:
    if not directory.is_dir():
        raise PublicationError("Synthetic bundle directory is missing.")
    files = {path.relative_to(directory).as_posix() for path in directory.rglob("*") if path.is_file()}
    if files != {"compose.yaml", "manifest.json"}:
        raise PublicationError("Synthetic bundle contains missing or unexpected files.")
    expected = {"compose.yaml": compose_sha256, "manifest.json": manifest_sha256}
    for name, digest in expected.items():
        path = directory / name
        if path.is_symlink() or not re.fullmatch(r"[0-9a-f]{64}", digest) or sha256_file(path) != digest:
            raise PublicationError(f"Synthetic bundle hash mismatch: {name}")
    try:
        artifact = json.loads(oci_manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise PublicationError("Synthetic OCI artifact manifest is missing or invalid.") from exc
    if (artifact.get("schemaVersion") != 2
            or artifact.get("mediaType") != "application/vnd.oci.image.manifest.v1+json"
            or artifact.get("artifactType") != "application/vnd.shutterspool.synthetic.registry-probe"
            or "subject" in artifact):
        raise PublicationError("Synthetic OCI artifact manifest has an unexpected identity or subject.")
    config = artifact.get("config", {})
    if (not isinstance(config, dict)
            or config.get("mediaType") != "application/vnd.oci.empty.v1+json"
            or config.get("digest") != "sha256:44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"
            or config.get("size") != 2
            or config.get("data") not in (None, "e30=")
            or set(config) - {"mediaType", "digest", "size", "data"}):
        raise PublicationError("Synthetic OCI artifact manifest does not use the expected empty config.")
    expected_layers = {
        "compose.yaml": ("application/yaml", compose_sha256),
        "manifest.json": ("application/json", manifest_sha256),
    }
    layers = artifact.get("layers")
    if not isinstance(layers, list) or len(layers) != len(expected_layers):
        raise PublicationError("Synthetic OCI artifact manifest has missing or unexpected layers.")
    observed = set()
    for layer in layers:
        title = layer.get("annotations", {}).get("org.opencontainers.image.title")
        if title not in expected_layers or title in observed:
            raise PublicationError("Synthetic OCI artifact manifest has an unexpected or duplicate layer.")
        media_type, digest = expected_layers[title]
        expected_path = directory / title
        if (layer.get("mediaType") != media_type or layer.get("digest") != f"sha256:{digest}"
                or layer.get("size") != expected_path.stat().st_size):
            raise PublicationError(f"Synthetic OCI artifact layer does not match expected bytes: {title}")
        observed.add(title)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("bundle")
    create.add_argument("--candidate", type=Path, required=True)
    create.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--bundle", type=Path, required=True)
    verify.add_argument("--version", required=True)
    verify.add_argument("--commit", required=True)
    verify.add_argument("--image-digest", required=True)
    layout = commands.add_parser("verify-image")
    layout.add_argument("--layout", type=Path, required=True)
    layout.add_argument("--image-digest", required=True)
    layout.add_argument("--require-attestations", action="store_true")
    synthetic = commands.add_parser("verify-synthetic-bundle")
    synthetic.add_argument("--directory", type=Path, required=True)
    synthetic.add_argument("--oci-manifest", type=Path, required=True)
    synthetic.add_argument("--compose-sha256", required=True)
    synthetic.add_argument("--manifest-sha256", required=True)
    inspect = commands.add_parser("inspect-tag")
    inspect.add_argument("registry_command", nargs=argparse.REMAINDER)
    plan = commands.add_parser("plan-tag")
    plan.add_argument("--existing", required=True, help="state=missing or a previously inspected digest=...")
    plan.add_argument("--approved-digest", required=True)
    tags = commands.add_parser("target-tags")
    tags.add_argument("--version", required=True)
    args = parser.parse_args()
    try:
        if args.command == "bundle":
            result = create_distribution_bundle(args.candidate, args.output)
        elif args.command == "verify":
            result = verify_distribution_bundle(args.bundle, version=args.version, commit=args.commit, image_digest=args.image_digest)
        elif args.command == "verify-image":
            predicates = verify_oci_layout(args.layout, expected_digest=args.image_digest, require_attestations=args.require_attestations)
            result = {"image_digest": args.image_digest, "attestations": ",".join(predicates)}
        elif args.command == "verify-synthetic-bundle":
            verify_synthetic_bundle(args.directory, oci_manifest=args.oci_manifest,
                                    compose_sha256=args.compose_sha256, manifest_sha256=args.manifest_sha256)
            print("Synthetic bundle contains exactly the two expected files with matching hashes.")
            return 0
        elif args.command == "target-tags":
            for name, value in public_version_tags(args.version).items():
                print(f"{name}={value}")
            return 0
        elif args.command == "inspect-tag":
            command = args.registry_command[1:] if args.registry_command[:1] == ["--"] else args.registry_command
            if not command:
                parser.error("inspect-tag requires a registry command after --")
            completed = subprocess.run(command, capture_output=True, text=True, check=False)
            state, digest = classify_registry_lookup(completed.returncode, completed.stdout + "\n" + completed.stderr)
            if state == "missing":
                print("state=missing")
                return 0
            if state != "present":
                raise PublicationError(f"Registry tag lookup is {state}; refusing to treat it as absent.")
            print(f"digest={digest}")
            return 0
        elif args.command == "plan-tag":
            existing = None if args.existing == "state=missing" else args.existing.removeprefix("digest=")
            print(plan_immutable_tag(existing, args.approved_digest))
            return 0
        for name, value in result.items():
            print(f"{name}={value}")
        return 0
    except PublicationError as exc:
        print(f"ghcr-publication: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
