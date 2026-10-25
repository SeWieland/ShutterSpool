# Release conventions

A ShutterSpool release has one shared product version and identifiable immutable artifacts.
A generated package or green build is not a published release or evidence of compatibility with every deployment.
See [package installation](../deploy/README.md) and [maintenance](../deploy/operations/README.md) for operational requirements.

## One version identifies the release

Use [Semantic Versioning](https://semver.org/) with one Git tag, `vX.Y.Z`.
First-party application components and deployment packages in that release share the version, including the Server and its bundled static Web output.
Do not create separate frontend, .NET project or package release sequences.

- During `0.x`, increment the patch for compatible fixes and the minor for features or breaking changes. State breaking changes explicitly; major zero is not a stable compatibility promise.
- Use a prerelease suffix, such as `v0.1.0-alpha.1`, for an unstable prerelease.
- At `1.0`, define supported API, configuration, deployment and upgrade contracts. Thereafter, incompatible changes require a major version, compatible additions a minor, and fixes a patch.
- Database migration identifiers and dependency versions remain separate facts. A shared product version does not prove upgrade compatibility.

## Artifact identity and compatibility

Derive artifact versions from one selected version and commit.
Record the source commit, artifact digests, dependency versions/digests, database compatibility and supported upgrade baseline in the release manifest.
The current database baseline is `initial-create` (`20261025211318_InitialCreate`). Packages declare `fresh-install-only` with no historical upgrade support. Candidate qualification remains pending until the exact artifact is verified; older upgrade rehearsals do not qualify this baseline.
Never move a published tag or replace its artifacts with different bytes; packaging fixes receive a new shared version.
Development builds identify their commit and unreleased status.

Publish only artifacts and notes explicitly approved by the maintainer.
Release publication does not itself authorize production deployment, DNS changes or data migration.
On a partial or uncertain publication, inspect the existing remote state before retrying.

Each release must state its supported platform/dependency profile and known limits.
Upgrade instructions identify a compatible predecessor and matching recovery custody; an image tag change alone is not a rollback procedure.
Do not infer physical-camera, provider, power-loss or restore-after-erasure guarantees from synthetic checks.

## Published notes

Keep one reviewed Markdown source for each published release entry and reuse that wording wherever notes appear.
Each entry identifies version/date, useful highlights, important fixes, upgrade actions or breaking changes, known limitations and the complete technical change list.
Omit empty sections and preserve security, media-loss and recovery consequences.
Describe observable improvements instead of narrating commits or internal classes.

AI-assisted drafts require factual review. Exclude planned work, unsupported compatibility, secrets and private media.
A short maintainer-supplied personal note is welcome when it helps explain the change; do not invent experiences or contributor credit.
Drafts and unreleased previews stay clearly distinguished from published releases.
Corrections to published notes are visibly identified and do not change the tagged software or artifact bytes.
