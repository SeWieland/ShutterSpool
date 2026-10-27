# Release conventions

A ShutterSpool release has one shared product version and identifiable artifacts.
A generated package or green build is not a published release or evidence of compatibility with every deployment.
Applicable license and security requirements, and truthful compatibility claims, apply to experimental releases too.
See [package installation](../deploy/README.md) and [maintenance](../deploy/operations/README.md) for operational requirements.

Pushing an eligible release tag authorizes publication of its matching container and complete GitHub Release.

## One version identifies the release

Use [Semantic Versioning](https://semver.org/) with one Git tag, `vX.Y.Z`.
First-party application components and deployment packages in that release share the version, including the Server and its bundled static Web output.
Do not create separate frontend, .NET project or package release sequences.

- Every `0.x` version and every version with a prerelease suffix, such as `v1.0.0-rc.1`, is experimental and does not carry the stable compatibility promise.
- Unsuffixed versions beginning at `1.0.0` are stable releases. State their supported API, configuration, deployment and upgrade contracts; thereafter, incompatible changes require a major version, compatible additions a minor, and fixes a patch.
- During `0.x`, increment the patch for fixes and the minor for features or breaking changes. State breaking changes explicitly.
- Database migration identifiers and dependency versions remain separate facts. A shared product version does not prove upgrade compatibility.

## Artifact identity and compatibility

Derive artifact versions from one selected version and commit.
The compact release manifest records version, source commit, platform, server image reference/digest, upstream images, database compatibility, upgrade baseline and distributed file inventory.
The current database baseline is `initial-create` (`20261025211318_InitialCreate`). Packages declare `fresh-install-only` with no historical upgrade support. Do not claim an upgrade path until that baseline is qualified.
Never move a published tag or replace its artifacts with different bytes; packaging fixes receive a new shared version.
Development builds identify their commit and unreleased status.

Publish only artifacts and notes explicitly approved by the maintainer.
Release publication does not itself authorize production deployment, DNS changes or data migration.
On a partial or uncertain publication, inspect the existing remote state before retrying.

Each release must state its supported platform/dependency profile and known limits.
Upgrade instructions identify a compatible predecessor and matching recovery custody; an image tag change alone is not a rollback procedure.
Do not infer physical-camera, provider, power-loss or restore-after-erasure guarantees from synthetic checks.

## Publish from a version tag

The [`release.yml` workflow](../.github/workflows/release.yml) runs for `vX.Y.Z` and `vX.Y.Z-suffix` tags on commits in `main`. After tag/source, required-CI and version checks, it creates a GitHub Release draft with GitHub-generated notes, uses the official Docker build-push action to publish the versioned Linux amd64 image directly to `ghcr.io/sewieland/shutterspool`, packages and attaches the Compose ZIP, verifies anonymous manifest retrieval and complete assets, then publishes the draft automatically. Stable qualification is complete before creating a stable tag.

The Compose configuration pins the image by digest. The Compose-only `tools/build-release.py` command takes version, revision and image digest inputs and does not build or export an image. No local image archive, private package, ORAS transfer, moving alias or candidate promotion is used.

GitHub's generated notes provide the initial release inventory. The tag authorizes the complete release; there is no separate manual publication gate. Experimental versions receive normal CI and basic package checks. Stable releases require qualification for their documented product and compatibility promises before tagging.

The workflow verifies anonymous image-manifest retrieval and complete release assets before publishing the release. GitHub and GHCR do not publish atomically: a failure may leave a public image or draft release. Available package files remain in a seven-day Actions artifact. Use standard GitHub facilities to inspect recovery; never rebuild over an existing version or replace different content. If recovery inputs are missing or their identity differs, stop for manual inspection. No custom resume system is provided.

GitHub defaults a newly created container package to private. If anonymous verification fails after an image upload, stop for maintainer inspection. The maintainer may [set the package to public](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility), then recover the partial release manually from the retained workflow outputs. Do not rerun publication over the existing image version. The workflow does not change package visibility.

Attach and verify both release assets before publishing. This also supports [immutable releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository): when enabled, assets cannot be added, replaced or deleted after publication. This workflow does not change that repository setting.

Operators download `shutterspool-compose.zip` and `shutterspool-compose.zip.sha256` from the GitHub Release, verify the checksum, extract the package, and use Docker Compose to pull the digest-pinned image. They do not need ORAS or registry credentials.

## Published notes

The published GitHub Release body is the canonical release-note text; future documentation consumes it without creating another editable copy.
Each entry identifies version/date, useful highlights, important fixes, upgrade actions or breaking changes, known limitations and the complete technical change list.
Omit empty sections and preserve security, media-loss and recovery consequences.
Describe observable improvements instead of narrating commits or internal classes.

AI-assisted drafts require factual review. Exclude planned work, unsupported compatibility, secrets and private media.
A short maintainer-supplied personal note is welcome when it helps explain the change; do not invent experiences or contributor credit.
Drafts and unreleased previews stay clearly distinguished from published releases.
Corrections to published notes are visibly identified and do not change the tagged software or artifact bytes.
