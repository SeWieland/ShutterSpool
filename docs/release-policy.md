# Release conventions

A ShutterSpool release has one shared product version and identifiable artifacts.
A generated package or green build is not a published release or evidence of compatibility with every deployment.
See [package installation](../deploy/README.md) and [maintenance](../deploy/operations/README.md) for operational requirements.

Pushing a release tag authorizes publication of its matching container and draft GitHub Release. The maintainer reviews the draft before publishing it.

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
Record the source commit, artifact digests, dependency versions/digests, database compatibility and supported upgrade baseline in the release manifest.
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

The [`release.yml` workflow](../.github/workflows/release.yml) runs for `vX.Y.Z` and `vX.Y.Z-suffix` tags on commits in `main`. It checks required CI, builds the tag's exact source once, publishes the versioned image to `ghcr.io/sewieland/shutterspool`, and opens a draft GitHub Release with the Compose archive and checksum attached. Build credentials have only package-read access; the separate publication job receives package-write access.

The Compose configuration pins the image by digest. The release manifest records the source commit, image digest, dependency identities and distributed-file inventory. The local image archive is not distributed in the Compose release archive. No private package, OCI bundle, moving alias or candidate promotion is used.

The maintainer reviews and edits the GitHub draft notes, then publishes the release. The initial notes inventory comes from GitHub's generated release notes; retries preserve an existing draft body. A draft is not an approved release. Experimental versions receive ordinary CI and package checks; stable releases also require qualification for their documented product and compatibility promises and truthful compatibility claims.

The workflow verifies the public image and release assets before reporting the draft ready. GitHub and GHCR do not publish atomically: an image may be public while its GitHub release remains a draft. Re-run failed jobs to resume from the retained Actions artifact; never rebuild over an existing version or replace different content. If the artifact is missing or its identity differs, stop for maintainer inspection.

GitHub defaults a newly created container package to private. If the first upload succeeds but anonymous verification fails, inspect the package's visibility. If it is private, the maintainer must [set it to public](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility), then rerun the failed publication job using its retained build. The workflow does not change package visibility.

Attach and verify both release assets before manually publishing the draft. This also supports [immutable releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository): when enabled, assets cannot be added, replaced or deleted after publication. This workflow does not change that repository setting.

Operators download `shutterspool-compose.tar.gz` and its `.sha256` file from the GitHub Release, verify the checksum, and use Docker Compose to pull the digest-pinned image. They do not need ORAS or registry credentials. ORAS is used inside the publishing workflow to transfer the locally built OCI image.

## Published notes

The reviewed GitHub Release body is the canonical release-note text; future documentation consumes it without creating another editable copy.
Each entry identifies version/date, useful highlights, important fixes, upgrade actions or breaking changes, known limitations and the complete technical change list.
Omit empty sections and preserve security, media-loss and recovery consequences.
Describe observable improvements instead of narrating commits or internal classes.

AI-assisted drafts require factual review. Exclude planned work, unsupported compatibility, secrets and private media.
A short maintainer-supplied personal note is welcome when it helps explain the change; do not invent experiences or contributor credit.
Drafts and unreleased previews stay clearly distinguished from published releases.
Corrections to published notes are visibly identified and do not change the tagged software or artifact bytes.
