# Release conventions

A ShutterSpool release has one shared product version and identifiable immutable artifacts.
A generated package or green build is not a published release or evidence of compatibility with every deployment.
See [package installation](../deploy/README.md) and [maintenance](../deploy/operations/README.md) for operational requirements.

Container publication does not turn an unqualified candidate into a release. No public ShutterSpool release image or Compose bundle is available yet.

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

## Stage and promote artifacts

The [private candidate workflow](../.github/workflows/candidate.yml) runs manually from `main`, with an explicit prerelease version and exact source commit on `main`.
It builds the image once and stages it with a Compose bundle in the private `ghcr.io/sewieland/shutterspool-candidates` package.
Staging requires `GHCR_CANDIDATE_STAGING_ENABLED=true`; promotion separately requires `GHCR_PUBLISHING_ENABLED=true`.
Both remain disabled while the workflow is being verified. Local building remains available through the [AppHost guide](../src/ShutterSpool.AppHost/README.md#prepare-a-compose-candidate).

Before enabling staging, confirm private package visibility, disabled repository-permission inheritance and explicit Actions access for this repository in the [personal package settings](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility#selecting-the-inheritance-setting-for-packages-scoped-to-your-personal-account).
Inspect user and Codespaces grants too, and verify authorized retrieval and anonymous denial with harmless objects.
GitHub [warns that public-repository Actions grants may permit fork access](https://docs.github.com/en/packages/managing-github-packages-using-github-actions-workflows/publishing-and-installing-a-package-with-github-actions#default-permissions-and-access-settings-for-packages-modified-through-workflows); these checks do not establish independent-account or fork isolation.
Both workflows check private visibility before accessing candidates. Pull requests have no package permissions or publication trigger.

The [promotion workflow](../.github/workflows/promote-container.yml) takes the qualified bundle digest and reviewed notes SHA-256.
It reads the version, source commit and image digest from that immutable bundle's OCI annotations before requesting approval through the protected `ghcr-publish` environment.
Configure that environment for the maintainer's required approval, permit self-approval, disable administrator bypass and restrict deployment branches to `main`.
The approval identifies both artifact digests, the notes hash and exact destinations. After approval, ORAS copies the same artifacts without rebuilding; its image-index copy includes the build's SBOM and provenance manifests.
Qualification of the selected candidate, including those attestations, happens before requesting promotion.

Image tags use the shared version, such as `0.1.0-alpha.1`; bundle tags use `bundle-0.1.0-alpha.1`. Installation uses recorded digests, with no moving aliases.
The serialized workflow checks both destination tags before copying either object. Matching digests are reused; conflicting content and lookup errors stop publication.
On the first publication, the maintainer may need to set the new release package's visibility to public and retry the same run. The workflow does not change package visibility.
A partial run remains incomplete until both digests match and the image and bundle have been retrieved without registry credentials.
Package administrators can still change tags outside this workflow; its collision protection is not registry-enforced immutability.

## Published notes

Keep one reviewed Markdown source for each published release entry and reuse that wording wherever notes appear.
Each entry identifies version/date, useful highlights, important fixes, upgrade actions or breaking changes, known limitations and the complete technical change list.
Omit empty sections and preserve security, media-loss and recovery consequences.
Describe observable improvements instead of narrating commits or internal classes.

AI-assisted drafts require factual review. Exclude planned work, unsupported compatibility, secrets and private media.
A short maintainer-supplied personal note is welcome when it helps explain the change; do not invent experiences or contributor credit.
Drafts and unreleased previews stay clearly distinguished from published releases.
Corrections to published notes are visibly identified and do not change the tagged software or artifact bytes.
