"""Build harmless synthetic OCI objects for the GHCR access probe."""

import hashlib
import json
import os
from pathlib import Path
import re
import argparse
import subprocess
import sys


REPOSITORY = "ghcr.io/sewieland/shutterspool-candidates"
IMAGE_MANIFEST_TYPE = "application/vnd.oci.image.manifest.v1+json"


def _write_blob(layout: Path, content: bytes) -> dict[str, object]:
    digest = "sha256:" + hashlib.sha256(content).hexdigest()
    blob = layout / "blobs" / "sha256" / digest.removeprefix("sha256:")
    blob.parent.mkdir(parents=True, exist_ok=True)
    blob.write_bytes(content)
    return {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": digest, "size": len(content)}


def create_probe(output: Path, *, run_id: str, run_attempt: str) -> dict[str, str]:
    if not re.fullmatch(r"[1-9][0-9]*", run_id) or not re.fullmatch(r"[1-9][0-9]*", run_attempt):
        raise ValueError("run id and attempt must be positive decimal numbers")

    output.mkdir(parents=True, exist_ok=False)
    layout = output / "image-layout"
    layout.mkdir()
    (layout / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}\n', encoding="utf-8")

    config_content = json.dumps({
        "architecture": "amd64", "os": "linux",
        "config": {"Labels": {"org.opencontainers.image.title": "SYNTHETIC REGISTRY ISOLATION PROBE"}},
        "rootfs": {"type": "layers", "diff_ids": []}, "history": [],
    }, separators=(",", ":")).encode("utf-8")
    config_descriptor = _write_blob(layout, config_content)
    manifest_content = json.dumps({
        "schemaVersion": 2, "mediaType": IMAGE_MANIFEST_TYPE,
        "config": config_descriptor, "layers": [],
    }, separators=(",", ":")).encode("utf-8")
    manifest_digest = "sha256:" + hashlib.sha256(manifest_content).hexdigest()
    manifest_blob = layout / "blobs" / "sha256" / manifest_digest.removeprefix("sha256:")
    manifest_blob.write_bytes(manifest_content)
    image_tag = f"isolation-{run_id}-{run_attempt}-image"
    descriptor = {
        "mediaType": IMAGE_MANIFEST_TYPE, "digest": manifest_digest, "size": len(manifest_content),
        "platform": {"architecture": "amd64", "os": "linux"},
        "annotations": {"org.opencontainers.image.ref.name": image_tag},
    }
    (layout / "index.json").write_text(json.dumps({"schemaVersion": 2, "manifests": [descriptor]}) + "\n", encoding="utf-8")

    compose = output / "compose.yaml"
    compose_content = (
        "# SYNTHETIC REGISTRY ISOLATION PROBE; never start this file.\n"
        "services:\n  probe:\n    image: example.invalid/synthetic-probe:never-run\n"
    ).encode("utf-8")
    compose.write_bytes(compose_content)
    compose_sha = hashlib.sha256(compose_content).hexdigest()
    manifest = output / "manifest.json"
    manifest_content = json.dumps({
        "kind": "synthetic-registry-isolation-probe", "candidate": False,
        "files": {"compose.yaml": {"sha256": compose_sha}},
    }, separators=(",", ":")).encode("utf-8") + b"\n"
    manifest.write_bytes(manifest_content)
    return {
        "repository": REPOSITORY, "image_tag": image_tag,
        "bundle_tag": f"isolation-{run_id}-{run_attempt}-bundle",
        "image_digest": manifest_digest,
        "image_layout": str(layout), "compose_file": str(compose), "manifest_file": str(manifest),
        "compose_sha256": compose_sha, "manifest_sha256": hashlib.sha256(manifest_content).hexdigest(),
    }


def is_registry_access_denial(returncode: int, output: str) -> bool:
    registry_denial = r"\b(?:401|403)\b|(?:unauthorized|denied)\s*:\s*(?:authentication required|requested access to the resource is denied|insufficient_scope)"
    return returncode != 0 and bool(re.search(registry_denial, output, re.IGNORECASE))


def login_control_result(returncode: int, output: str) -> str:
    if returncode == 0:
        return "available"
    if is_registry_access_denial(returncode, output):
        return "unavailable"
    return "error"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    create = subparsers.add_parser("create", help="create local synthetic image and bundle files")
    create.add_argument("--output", type=Path, required=True)
    create.add_argument("--run-id", required=True)
    create.add_argument("--run-attempt", required=True)
    denial = subparsers.add_parser("verify-denial", help="require a registry authorization denial")
    denial.add_argument("registry_command", nargs=argparse.REMAINDER)
    token_login = subparsers.add_parser("verify-token-login", help="classify the package-scope-negative token login")
    token_login.add_argument("registry_command", nargs=argparse.REMAINDER)
    args = parser.parse_args()

    if args.command == "create":
        try:
            result = create_probe(args.output, run_id=args.run_id, run_attempt=args.run_attempt)
        except (FileExistsError, ValueError) as exc:
            print(f"registry-isolation: {exc}", file=sys.stderr)
            return 1
        for name, value in result.items():
            print(f"{name}={value}")
        return 0

    command = args.registry_command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        parser.error(f"{args.command} requires a registry command after --")
    if args.command == "verify-token-login":
        result = subprocess.run(command, text=True, capture_output=True, check=False, stdin=sys.stdin)
        status = login_control_result(result.returncode, result.stdout + "\n" + result.stderr)
        if status == "error":
            print("GHCR token login failed without a clear authorization response; control is unverified.", file=sys.stderr)
            return 1
        output_path = Path(os.environ["GITHUB_OUTPUT"])
        with output_path.open("a", encoding="utf-8") as output:
            output.write(f"available={'true' if status == 'available' else 'false'}\n")
        print("The package-permission-absent token can authenticate; checking its package access." if status == "available" else "GHCR rejected the token during login; authenticated package access remains unverified.")
        return 0
    result = subprocess.run(command, text=True, capture_output=True, check=False)
    combined_output = result.stdout + "\n" + result.stderr
    if not is_registry_access_denial(result.returncode, combined_output):
        if result.returncode == 0:
            print("Registry read succeeded in a context expected to be denied; isolation failed.", file=sys.stderr)
        else:
            status = re.search(r"\b[45][0-9]{2}\b", combined_output)
            detail = f"; HTTP status {status.group()}" if status else ""
            print(f"Registry command failed with exit code {result.returncode}{detail}, without a clear authorization denial; isolation is unverified.", file=sys.stderr)
        return 1
    print("Registry access returned an explicit authorization denial.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
