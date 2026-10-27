#!/usr/bin/env python3
"""Package Aspire Compose files for an already built Server image."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APPHOST = ROOT / "src/ShutterSpool.AppHost/ShutterSpool.AppHost.csproj"
IMAGE_REPOSITORY = "ghcr.io/sewieland/shutterspool"
SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$")
DATABASE_COMPATIBILITY = {
    "baseline": "initial-create",
    "initialMigration": "20261025211318_InitialCreate",
    "historicalUpgradeSupported": False,
}
UPGRADE_BASELINE = {"kind": "fresh-install-only", "publishedRelease": None}
COMPOSE_VARIABLES = {
    "BACKEND_SUBNET", "COMPOSE_PROJECT_NAME", "HTTPS_LISTEN_ADDRESS", "HTTPS_PORT",
    "IDENTITY_PROXY_SUBNET", "INGEST_ADVERTISED_ADDRESS", "INGEST_HOSTNAME", "INGEST_LISTEN_ADDRESS",
    "POSTGRES_DB", "POSTGRES_INIT_SKIP_APP", "POSTGRES_USER", "PROXY_NETWORK", "PROXY_SUBNET", "SERVER_IMAGE",
}


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
    match = SEMVER.fullmatch(version)
    if not match or any(identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0")
                        for identifier in (match.group(4) or "").split(".")) or len(version) > 128:
        raise BuildError("Version must be OCI-compatible SemVer without build metadata, such as 0.1.0-alpha.1 or 1.0.0.")


def is_prerelease(version: str) -> bool:
    validate_version(version)
    return version.startswith("0.") or "-" in version


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def image_refs(compose_file: Path) -> list[str]:
    # Keep required-value interpolation intact; its explanatory message may contain spaces.
    refs = []
    for line in compose_file.read_text(encoding="utf-8").splitlines():
        match = re.match(r"\s*image:\s*(.+?)\s*(?:#.*)?$", line)
        if match:
            refs.append(match.group(1).strip("'\""))
    return sorted(set(refs))


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
    packages = [*output.rglob("docker-compose.yaml"), *output.rglob("docker-compose.yml")]
    if len(packages) != 1:
        raise BuildError(f"Expected one generated Compose file for {sorted(enabled_profiles)}, found {len(packages)}.")
    compose = packages[0]
    service_names = set(re.findall(r"(?m)^  ([a-zA-Z0-9_-]+):\s*$", compose.read_text(encoding="utf-8")))
    expected = {"ModernFtps": "camera-edge", "LegacyFtps": "camera-edge-legacy", "PlainFtpLan": "camera-edge-plain"}
    required = {"server", *(edge for profile, edge in expected.items() if profile in enabled_profiles)}
    missing = sorted(required - service_names)
    if missing:
        raise BuildError("Aspire publish omitted required services: " + ", ".join(missing))
    return compose


def prepare(args: argparse.Namespace) -> Path:
    validate_version(args.version)
    if not re.fullmatch(r"[0-9a-f]{40}", args.revision):
        raise BuildError("Revision must be the exact 40-character Git SHA.")
    if run(["git", "rev-parse", "HEAD"]) != args.revision:
        raise BuildError("Selected revision does not match checkout HEAD.")
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", args.image_digest):
        raise BuildError("Image digest must be a SHA-256 registry manifest digest.")
    stage = args.output.resolve()
    stage.mkdir(parents=True)  # Refuse existing output; failed packages remain available for inspection.
    with tempfile.TemporaryDirectory(prefix="shutterspool-compose-") as temp:
        (stage / "profiles").mkdir()
        variants = [
            ("compose.yaml", {"ModernFtps"}),
            ("profiles/all-camera-edges.yaml", {"ModernFtps", "LegacyFtps", "PlainFtpLan"}),
        ]
        upstream_images = set()
        for relative, enabled_profiles in variants:
            compose = publish_compose(args, Path(temp) / ("aspire-" + Path(relative).stem), enabled_profiles)
            upstream_images.update(ref for ref in validate_compose(compose) if "${" not in ref)
            shutil.copy2(compose, stage / relative)
        (stage / "package").mkdir()
        for helper in ("edge-entrypoint.sh", "identity-entrypoint.sh", "postgres-init.sh"):
            shutil.copy2(ROOT / "deploy/package" / helper, stage / "package" / helper)
        shutil.copytree(ROOT / "src/ShutterSpool.AppHost/Realms", stage / "package/realms")
        shutil.copytree(ROOT / "src/ShutterSpool.AppHost/Themes/shutterspool", stage / "package/theme")
        shutil.copytree(ROOT / "deploy/package/traefik", stage / "package/traefik")
        for docs in ("certificates", "operations"):
            (stage / docs).mkdir()
            shutil.copy2(ROOT / "deploy" / docs / "README.md", stage / docs / "README.md")
        for relative in ("components", "proxies"):
            shutil.copytree(ROOT / "deploy/package" / relative, stage / relative)
        for overlay in sorted([*stage.glob("components/*.yaml"), *stage.glob("proxies/*.yaml")]):
            upstream_images.update(ref for ref in validate_compose(overlay) if "${" not in ref)
        (stage / "config").mkdir()
        for example in sorted((ROOT / "deploy/config").glob("*.example")):
            shutil.copy2(example, stage / "config" / example.name)
            shutil.copy2(example, stage / "config" / example.name.removesuffix(".example"))
        shutil.copy2(ROOT / "deploy/README.md", stage / "README.md")
        shutil.copy2(ROOT / "deploy/.env.example", stage / ".env.example")

    image = f"{IMAGE_REPOSITORY}:{args.version}"
    env_template = (stage / ".env.example").read_text(encoding="utf-8")
    env_template = env_template.replace("SHUTTERSPOOL_VERSION=REPLACE_WITH_RELEASE_VERSION", f"SHUTTERSPOOL_VERSION={args.version}")
    env_template = env_template.replace("SERVER_IMAGE=REPLACE_WITH_IMAGE_DIGEST", f"SERVER_IMAGE={image}@{args.image_digest}")
    (stage / ".env").write_text(env_template, encoding="utf-8")
    files = sorted([path.relative_to(stage).as_posix() for path in stage.rglob("*") if path.is_file()]
                   + ["release-manifest.json"])
    manifest = {
        "format": 2, "version": args.version, "commit": args.revision,
        "prerelease": is_prerelease(args.version), "platform": "linux/amd64",
        "serverImage": {"reference": image, "digest": args.image_digest},
        "upstreamImages": sorted(upstream_images),
        "databaseCompatibility": DATABASE_COMPATIBILITY,
        "upgradeBaseline": UPGRADE_BASELINE,
        "files": files,
    }
    (stage / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    archive = stage / "shutterspool-compose.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
        for relative in files:
            bundle.write(stage / relative, relative)
    archive.with_suffix(".zip.sha256").write_text(f"{sha256_file(archive)}  {archive.name}\n", encoding="utf-8")
    return stage


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--revision", required=True, help="Exact source commit SHA")
    parser.add_argument("--image-digest", required=True, help="Digest returned by the image build")
    parser.add_argument("--output", required=True, type=Path, help="New Compose package directory")
    parser.add_argument("--aspire", default="aspire")
    args = parser.parse_args()
    try:
        print(prepare(args))
        return 0
    except (BuildError, OSError) as exc:
        print(f"build-release: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
