#!/usr/bin/env python3
"""Prepare a private, local linux/amd64 ShutterSpool candidate artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
APPHOST = ROOT / "src/ShutterSpool.AppHost/ShutterSpool.AppHost.csproj"
DOCKERFILE = ROOT / "deploy/Server.Dockerfile"
SEMVER_PRERELEASE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")
ASPIRE_CLI_VERSION = "13.6.0"
DATABASE_COMPATIBILITY = {
    "baseline": "initial-create",
    "initialMigration": "20261025211318_InitialCreate",
    "historicalUpgradeSupported": False,
    "qualification": "pending",
}
UPGRADE_BASELINE = {"kind": "fresh-install-only", "publishedRelease": None, "qualification": "not-supported"}
LFS_HEADER = b"version https://git-lfs.github.com/spec/v1\n"
COMPOSE_VARIABLES = {
    "BACKEND_SUBNET", "COMPOSE_PROJECT_NAME", "HTTPS_LISTEN_ADDRESS", "HTTPS_PORT",
    "IDENTITY_PROXY_SUBNET", "INGEST_ADVERTISED_ADDRESS", "INGEST_HOSTNAME", "INGEST_LISTEN_ADDRESS",
    "POSTGRES_DB", "POSTGRES_INIT_SKIP_APP", "POSTGRES_USER", "PROXY_NETWORK", "PROXY_SUBNET", "SERVER_IMAGE",
}
BUILD_SOURCE_PREFIXES = ("src/ShutterSpool.AppHost/", "src/ShutterSpool.Domain/", "src/ShutterSpool.Application/",
                         "src/ShutterSpool.Infrastructure/", "src/ShutterSpool.ServiceDefaults/",
                         "src/ShutterSpool.Server/", "src/ShutterSpool.Web/", "deploy/", "tools/build-release.py")
BUILD_SOURCE_FILES = {"global.json", "Directory.Build.props", ".dockerignore"}


class BuildError(RuntimeError):
    pass


def run(command: list[str], *, cwd: Path = ROOT, env: dict[str, str] | None = None) -> str:
    try:
        result = subprocess.run(command, cwd=cwd, env=env, check=True, text=True,
                                encoding="utf-8", errors="replace",
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    except (OSError, subprocess.CalledProcessError) as exc:
        output = getattr(exc, "stdout", None)
        raise BuildError(f"Command failed: {' '.join(command)}\n{output or exc}") from exc
    return result.stdout.strip()


def validate_version(version: str) -> None:
    match = SEMVER_PRERELEASE.fullmatch(version)
    if not match or any(identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0")
                        for identifier in match.group(4).split(".")):
        raise BuildError("Candidate version must be an explicit SemVer prerelease, such as 0.1.0-alpha.1.")


def required_sdk_version(global_json: Path = ROOT / "global.json") -> tuple[int, int, int]:
    try:
        sdk = json.loads(global_json.read_text(encoding="utf-8"))["sdk"]
        version = sdk["version"]
        parts = tuple(int(part) for part in version.split("."))
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise BuildError(f"Cannot read a numeric SDK version from {global_json}.") from exc
    if len(parts) != 3 or sdk.get("rollForward") != "latestPatch":
        raise BuildError("Candidate builds require a three-part SDK version with the existing latestPatch roll-forward policy.")
    return parts


def validate_sdk_version(actual: str, required: tuple[int, int, int]) -> str:
    try:
        found = tuple(int(part) for part in actual.split("."))
    except ValueError as exc:
        raise BuildError(f"Cannot parse installed .NET SDK version: {actual}.") from exc
    same_feature_band = len(found) == 3 and found[:2] == required[:2] and found[2] // 100 == required[2] // 100
    if not same_feature_band or found[2] < required[2]:
        required_text = ".".join(map(str, required))
        raise BuildError(f"Installed .NET SDK {actual} is incompatible with global.json version {required_text} (rollForward: latestPatch).")
    return actual


def validate_aspire_cli_version(actual: str) -> str:
    if actual.split("+", maxsplit=1)[0] != ASPIRE_CLI_VERSION:
        raise BuildError(f"Candidate builds require Aspire CLI {ASPIRE_CLI_VERSION}; found {actual}.")
    return actual


def is_lfs_pointer(path: Path) -> bool:
    with path.open("rb") as source:
        return source.read(len(LFS_HEADER)) == LFS_HEADER


def source_identity(allow_dirty: bool) -> tuple[str, bool, dict[str, str]]:
    revision = run(["git", "rev-parse", "HEAD"]).lower()
    status = run(["git", "status", "--porcelain", "--untracked-files=all"])
    if status and not allow_dirty:
        raise BuildError("Source tree is dirty; commit the candidate inputs or use --allow-dirty for a development rehearsal.")
    if status:
        print("Development rehearsal: source tree is dirty; output will be labelled unreleased.", file=sys.stderr)
    tracked = run(["git", "ls-files", "-co", "--exclude-standard", "-z"]).split("\0")
    for relative in filter(None, tracked):
        path = ROOT / relative
        if path.is_file() and is_lfs_pointer(path):
            raise BuildError(f"Git LFS pointer is present instead of hydrated content: {relative}")
    return revision, bool(status), source_snapshot(ROOT, build_source_paths(tracked))


def validate_selection(version: str, selected_revision: str, head_revision: str, dirty: bool, allow_dirty: bool) -> None:
    validate_version(version)
    if selected_revision.lower() != head_revision.lower():
        raise BuildError(f"Selected revision {selected_revision} does not match checkout HEAD {head_revision}.")
    if dirty and not allow_dirty:
        raise BuildError("Source tree is dirty; commit the candidate inputs or use --allow-dirty for a development rehearsal.")


def copy_tree(source: Path, destination: Path) -> None:
    if not source.is_dir():
        raise BuildError(f"Required artifact input is missing: {source.relative_to(ROOT)}")
    shutil.copytree(source, destination)


def copy_operator_guide(source: Path, destination: Path) -> Path:
    if not source.is_file():
        raise BuildError(f"Required operator guide is missing: {source}")
    destination.mkdir(parents=True, exist_ok=True)
    target = destination / "README.md"
    shutil.copy2(source, target)
    return target


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def source_snapshot(root: Path, relative_paths: list[str]) -> dict[str, str]:
    snapshot = {}
    for relative in sorted(filter(None, relative_paths)):
        path = root / relative
        if not path.is_file():
            continue
        snapshot[relative.replace("\\", "/")] = sha256_file(path)
    return snapshot


def fingerprint_files(root: Path, relative_paths: list[str]) -> str:
    return fingerprint_snapshot(source_snapshot(root, relative_paths))


def fingerprint_snapshot(snapshot: dict[str, str]) -> str:
    digest = hashlib.sha256()
    for relative, sha256 in sorted(snapshot.items()):
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(sha256.encode("ascii"))
        digest.update(b"\0")
    return digest.hexdigest()


def changed_paths(before: dict[str, str], after: dict[str, str]) -> list[str]:
    return sorted(path for path in before.keys() | after.keys() if before.get(path) != after.get(path))


def build_source_paths(relative_paths: list[str]) -> list[str]:
    ignored_parts = {"bin", "obj", "artifacts", "TestResults", "__pycache__"}
    return [relative for relative in relative_paths
            if relative in BUILD_SOURCE_FILES or relative.startswith(BUILD_SOURCE_PREFIXES)
            if not ignored_parts.intersection(Path(relative).parts)]


def verify_source_unchanged(revision: str, initial: dict[str, str], allow_dirty: bool) -> dict[str, str]:
    if run(["git", "rev-parse", "HEAD"]).lower() != revision:
        raise BuildError("Git HEAD changed during candidate preparation.")
    paths = run(["git", "ls-files", "-co", "--exclude-standard", "-z"]).split("\0")
    current = source_snapshot(ROOT, build_source_paths(paths))
    changed = changed_paths(initial, current)
    if changed and not allow_dirty:
        raise BuildError("Source files changed during candidate preparation: " + ", ".join(changed))
    return current


def verify_oci_attestations(archive: Path, expected_digest: str) -> list[str]:
    def read_json(bundle: tarfile.TarFile, name: str) -> dict[str, object]:
        member = bundle.extractfile(name)
        if member is None:
            raise BuildError(f"OCI candidate is missing {name}.")
        return json.load(member)

    with tarfile.open(archive, "r") as bundle:
        index = read_json(bundle, "index.json")
        top = next((item for item in index.get("manifests", []) if item.get("digest") == expected_digest), None)
        if top is None:
            raise BuildError("Buildx digest does not identify a manifest in the OCI archive index.")
        nested_name = "blobs/sha256/" + expected_digest.removeprefix("sha256:")
        nested_blob = bundle.extractfile(nested_name)
        if nested_blob is None:
            raise BuildError("OCI candidate manifest blob is missing.")
        nested_bytes = nested_blob.read()
        if hashlib.sha256(nested_bytes).hexdigest() != expected_digest.removeprefix("sha256:"):
            raise BuildError("OCI candidate manifest digest does not match its content.")
        nested = json.loads(nested_bytes)
        attestation = next((item for item in nested.get("manifests", [])
                            if item.get("annotations", {}).get("vnd.docker.reference.type") == "attestation-manifest"), None)
        if attestation is None:
            raise BuildError("Buildx OCI output has no attestation manifest.")
        attestation_name = "blobs/sha256/" + attestation["digest"].removeprefix("sha256:")
        attestation_manifest = read_json(bundle, attestation_name)
        predicates = set()
        for layer in attestation_manifest.get("layers", []):
            predicate = layer.get("annotations", {}).get("in-toto.io/predicate-type")
            if predicate:
                payload_name = "blobs/sha256/" + layer["digest"].removeprefix("sha256:")
                statement = read_json(bundle, payload_name)
                if statement.get("predicateType") == predicate:
                    predicates.add(predicate)
    required = {"https://spdx.dev/Document", "https://slsa.dev/provenance/v1"}
    missing = required - predicates
    if missing:
        raise BuildError("OCI candidate lacks required SBOM/provenance attestations: " + ", ".join(sorted(missing)))
    return sorted(predicates)


def file_manifest(directory: Path) -> dict[str, dict[str, object]]:
    return {path.relative_to(directory).as_posix(): {"size": path.stat().st_size, "sha256": sha256_file(path)}
            for path in sorted(directory.rglob("*")) if path.is_file() and path.name != "release-manifest.json"}


def image_refs(compose_file: Path) -> list[str]:
    # Keep required-value interpolation intact; its explanatory message may contain spaces.
    refs = []
    for line in compose_file.read_text(encoding="utf-8").splitlines():
        match = re.match(r"\s*image:\s*(.+?)\s*(?:#.*)?$", line)
        if match:
            refs.append(match.group(1).strip("'\""))
    return sorted(set(refs))


def base_images(dockerfile: Path, build_args: dict[str, str] | None = None) -> list[str]:
    refs = re.findall(r"^FROM\s+(?:\$\{([A-Z_]+)\}|([^\s]+))", dockerfile.read_text(encoding="utf-8"), re.MULTILINE)
    names = dict(re.findall(r"^ARG\s+([A-Z_]+)=([^\s]+)", dockerfile.read_text(encoding="utf-8"), re.MULTILINE))
    names.update(build_args or {})
    images = set()
    for key, literal in refs:
        image = names.get(key, "") if key else literal
        image = re.sub(r"\$\{([A-Z_]+)\}", lambda match: names.get(match.group(1), ""), image)
        images.add(image)
    images = sorted(images)
    missing = [image for image in images if not image or "@sha256:" not in image]
    if missing:
        raise BuildError("Every Dockerfile base image must be pinned by digest before building: " + ", ".join(missing))
    return images


def parse_docker_versions(output: str) -> tuple[str, str]:
    client, separator, server = output.strip().partition("|")
    if not separator or not client.strip() or not server.strip():
        raise BuildError("Docker client and server versions must both be available for a reproducible candidate build.")
    return client.strip(), server.strip()


def build_environment(args: argparse.Namespace) -> dict[str, str]:
    client, server = parse_docker_versions(
        run([args.docker, "version", "--format", "{{.Client.Version}}|{{.Server.Version}}"]))
    sdk_version = run(["dotnet", "--version"])
    validate_sdk_version(sdk_version, required_sdk_version())
    return {
        "aspireCli": validate_aspire_cli_version(run([args.aspire, "--version"])),
        "dotnetSdk": sdk_version,
        "dockerClient": client,
        "dockerServer": server,
        "buildx": run([args.docker, "buildx", "version"]),
    }


def validate_compose(compose_file: Path) -> list[str]:
    text = compose_file.read_text(encoding="utf-8")
    if re.search(r"(?<![\w])\.\.?/src(?:/|:|$)", text):
        raise BuildError("Generated Compose contains a source-tree mount.")
    variables = set(re.findall(r"\$\{([A-Z][A-Z0-9_]*)", text))
    unresolved = variables - COMPOSE_VARIABLES
    if unresolved:
        raise BuildError("Generated Compose contains unresolved non-operator parameters: " + ", ".join(sorted(unresolved)))
    refs = image_refs(compose_file)
    placeholder = "${SERVER_IMAGE:?Select the qualified Server image}"
    unpinned = [ref for ref in refs if ref != placeholder and "@sha256:" not in ref]
    if unpinned:
        raise BuildError("Every upstream Compose image must be digest-pinned: " + ", ".join(unpinned))
    return refs


def publish_environment(enabled_profiles: set[str]) -> dict[str, str]:
    env = os.environ.copy()
    env["ShutterSpool__CameraIngress__CertificateDirectory"] = "./certificates"
    for profile in ("ModernFtps", "LegacyFtps", "PlainFtpLan"):
        env[f"ShutterSpool__CameraIngress__Profiles__{profile}__Enabled"] = str(profile in enabled_profiles).lower()
    return env


def publish_compose(args: argparse.Namespace, output: Path, enabled_profiles: set[str]) -> Path:
    output.mkdir(parents=True)
    run([args.aspire, "publish", "--apphost", str(APPHOST), "--output-path", str(output), "--non-interactive"],
        env=publish_environment(enabled_profiles))
    candidates = [*output.rglob("docker-compose.yaml"), *output.rglob("docker-compose.yml")]
    if len(candidates) != 1:
        raise BuildError(f"Expected one generated Compose file for {sorted(enabled_profiles)}, found {len(candidates)}.")
    compose = candidates[0]
    refs = validate_compose(compose)
    if not {"camera-edge", "server"}.issubset(set(re.findall(r"(?m)^  ([a-zA-Z0-9_-]+):\s*$", compose.read_text(encoding="utf-8")))):
        raise BuildError("Aspire publish omitted the required Server or camera-edge service.")
    service_names = set(re.findall(r"(?m)^  ([a-zA-Z0-9_-]+):\s*$", compose.read_text(encoding="utf-8")))
    expected = {"ModernFtps": "camera-edge", "LegacyFtps": "camera-edge-legacy", "PlainFtpLan": "camera-edge-plain"}
    missing = [edge for profile, edge in expected.items() if profile in enabled_profiles and edge not in service_names]
    if missing:
        raise BuildError("Aspire publish omitted explicitly enabled camera edges: " + ", ".join(missing))
    return compose


def prepare(args: argparse.Namespace) -> Path:
    validate_aspire_cli_version(run([args.aspire, "--version"]))
    sdk_image_version = ".".join(map(str, required_sdk_version()))
    sdk_host_version = validate_sdk_version(run(["dotnet", "--version"]), required_sdk_version())
    revision, dirty, source_at_start = source_identity(args.allow_dirty)
    validate_selection(args.version, args.revision, revision, dirty, args.allow_dirty)
    output = args.output.resolve()
    if output.exists():
        raise BuildError(f"Refusing to overwrite existing candidate artifacts: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="shutterspool-candidate-", dir=output.parent) as temp:
        stage = Path(temp) / "candidate"
        stage.mkdir()
        (stage / "profiles").mkdir()
        variants = [
            ("compose.yaml", {"ModernFtps"}),
            ("profiles/all-camera-edges.yaml", {"ModernFtps", "LegacyFtps", "PlainFtpLan"}),
        ]
        upstream_images = set()
        for relative, enabled_profiles in variants:
            compose = publish_compose(args, Path(temp) / ("aspire-" + Path(relative).stem), enabled_profiles)
            verify_source_unchanged(revision, source_at_start, args.allow_dirty)
            upstream_images.update(ref for ref in validate_compose(compose) if "${" not in ref)
            shutil.copy2(compose, stage / relative)
        (stage / "package").mkdir()
        for helper in ("edge-entrypoint.sh", "identity-entrypoint.sh", "postgres-init.sh"):
            shutil.copy2(ROOT / "deploy/package" / helper, stage / "package" / helper)
        copy_tree(ROOT / "src/ShutterSpool.AppHost/Realms", stage / "package/realms")
        copy_tree(ROOT / "src/ShutterSpool.AppHost/Themes/shutterspool", stage / "package/theme")
        copy_tree(ROOT / "deploy/package/traefik", stage / "package/traefik")
        for docs in ("certificates", "operations"):
            (stage / docs).mkdir()
            shutil.copy2(ROOT / "deploy" / docs / "README.md", stage / docs / "README.md")
        for relative in ("components", "proxies"):
            copy_tree(ROOT / "deploy/package" / relative, stage / relative)
        for overlay in sorted([*stage.glob("components/*.yaml"), *stage.glob("proxies/*.yaml")]):
            upstream_images.update(ref for ref in validate_compose(overlay) if "${" not in ref)
        (stage / "config").mkdir()
        for example in sorted((ROOT / "deploy/config").glob("*.example")):
            shutil.copy2(example, stage / "config" / example.name)
            shutil.copy2(example, stage / "config" / example.name.removesuffix(".example"))
        copy_operator_guide(ROOT / "deploy/README.md", stage)
        shutil.copy2(ROOT / "deploy/.env.example", stage / ".env.example")
        verify_source_unchanged(revision, source_at_start, args.allow_dirty)
        metadata_file = Path(temp) / "build-metadata.json"
        image = f"ghcr.io/shutterspool/shutterspool:{args.version}"
        run([args.docker, "buildx", "build", "--platform", "linux/amd64", "--provenance=true", "--sbom=true",
             "--output", f"type=oci,dest={stage / 'server-image.oci.tar'}", "--metadata-file", str(metadata_file),
             "--build-arg", f"SDK_VERSION={sdk_image_version}", "--build-arg", f"VERSION={args.version}",
             "--build-arg", f"COMMIT={revision}", "--tag", image,
             "--file", str(DOCKERFILE), str(ROOT)])
        metadata = json.loads(metadata_file.read_text(encoding="utf-8"))
        digest = metadata.get("containerimage.digest")
        if not isinstance(digest, str) or not digest.startswith("sha256:"):
            raise BuildError("Buildx did not report the local OCI image digest.")
        attestations = verify_oci_attestations(stage / "server-image.oci.tar", digest)
        build_tools = build_environment(args)
        source_at_finish = verify_source_unchanged(revision, source_at_start, args.allow_dirty)
        env_template = (ROOT / "deploy/.env.example").read_text(encoding="utf-8")
        env_template = env_template.replace("SHUTTERSPOOL_VERSION=REPLACE_WITH_CANDIDATE_VERSION", f"SHUTTERSPOOL_VERSION={args.version}")
        env_template = env_template.replace("SERVER_IMAGE=REPLACE_WITH_QUALIFIED_IMAGE_DIGEST", f"SERVER_IMAGE={image}@{digest}")
        (stage / ".env").write_text(env_template, encoding="utf-8")
        manifest = {
            "format": 1, "version": args.version, "commit": revision,
            "candidateStatus": "development-rehearsal" if dirty or changed_paths(source_at_start, source_at_finish) else "unreleased-candidate",
            "qualification": "pending", "qualificationNote": "A successful artifact build does not qualify installation, upgrade, recovery, host, or public-ingress claims.",
            "buildInputs": {"startSha256": fingerprint_snapshot(source_at_start),
                            "finishSha256": fingerprint_snapshot(source_at_finish),
                            "hasConcurrentChanges": bool(changed_paths(source_at_start, source_at_finish)),
                            "changedFiles": changed_paths(source_at_start, source_at_finish)},
            "platform": "linux/amd64", "serverImage": {"reference": image, "digest": digest, "artifact": "server-image.oci.tar"},
            "attestations": attestations,
            "buildEnvironment": build_tools,
            "sdkImageVersion": sdk_image_version, "sdkHostVersion": sdk_host_version,
            "upstreamImages": sorted(upstream_images),
            "buildBaseImages": base_images(DOCKERFILE, {"SDK_VERSION": sdk_image_version}),
            "databaseCompatibility": DATABASE_COMPATIBILITY,
            "upgradeBaseline": UPGRADE_BASELINE,
            "artifacts": file_manifest(stage),
        }
        (stage / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        final_snapshot = verify_source_unchanged(revision, source_at_start, args.allow_dirty)
        changed = changed_paths(source_at_start, final_snapshot)
        manifest["buildInputs"]["finishSha256"] = fingerprint_snapshot(final_snapshot)
        manifest["buildInputs"]["hasConcurrentChanges"] = bool(changed)
        manifest["buildInputs"]["changedFiles"] = changed
        if changed:
            manifest["candidateStatus"] = "development-rehearsal"
            print("Development rehearsal warning: build inputs changed during preparation: " + ", ".join(changed), file=sys.stderr)
        (stage / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        stage.rename(output)
    return output


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--revision", required=True, help="Exact 40-character Git SHA to build")
    parser.add_argument("--output", required=True, type=Path, help="New local candidate artifact directory")
    parser.add_argument("--aspire", default="aspire")
    parser.add_argument("--docker", default="docker")
    parser.add_argument("--allow-dirty", action="store_true", help="Allow a labelled, unreleased development rehearsal")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-fA-F]{40}", args.revision):
        parser.error("--revision must be the exact 40-character Git SHA")
    try:
        print(prepare(args))
        return 0
    except BuildError as exc:
        print(f"build-release: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
