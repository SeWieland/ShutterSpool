# Test guidance

Choose a check by the claim you need to prove: a pure policy decision, a real persistence/recovery boundary, or an architectural dependency.
Fast substitutes are useful for workflows, but they cannot establish a filesystem or PostgreSQL durability guarantee.

## CI frequency

All pull requests, including stacked changes targeting another feature branch, run `Dependency review`, which rejects newly introduced dependencies with known high or critical vulnerabilities, including development dependencies.
Dependabot checks GitHub Actions, npm, NuGet and Docker images weekly; updates still require the normal checks and maintainer review.
Docker scans the literal build-stage references in `deploy/Server.Dockerfile`, the shared `deploy/images/compose.yaml` pins, and the packaged component/proxy YAML files. Tags identify the update stream; digests remain the executable identity. SDK updates must still satisfy `global.json`, and database major-version changes need a supported migration path.
GitHub-managed CodeQL scans Actions, C#, JavaScript/TypeScript and Python on supported pushes and pull requests and on its scheduled runs. Static analysis complements the checks below; it does not qualify camera or recovery behavior.
The [CI workflow](../.github/workflows/ci.yml) runs SonarQube analysis on `main` pushes and same-repository pull requests targeting `main`. Fork and Dependabot changes are scanned after merge.
On noncancelled runs, CI uploads available coverage even when tests fail. Eligible SonarQube analysis imports the .NET XML report and a single frontend `lcov.info` when available.
The frontend report maps coverage to the original TypeScript and Vue files. Generated clients and build output are excluded.
SonarQube Cloud reports an advisory quality gate separately.

Pushes to `main` and all pull requests run the .NET suites with PostgreSQL, frontend type/test/build checks, migration and generated-client consistency checks, camera-client security, edge certificate validation and release-builder checks.
The release-builder tests use temporary fixtures and perform no application builds or registry writes.
The [attribution checks](../src/ShutterSpool.Web/README.md#build-and-check) exercise metadata generation with controlled registry responses and run as part of `npm test`. Frontend builds resolve the pinned SFTPGo metadata and require outbound access to Docker Hub.
Version tags on commits in `main` with successful CI run the [`release.yml` workflow](../.github/workflows/release.yml), which builds and pushes the image with the official Docker action, packages the Compose ZIP, checks anonymous manifest retrieval and release assets, then publishes the GitHub Release.
Available package files are retained as a seven-day Actions artifact. Offline tests do not establish live GHCR access or anonymous retrieval. See the [release workflow conventions](../docs/release-policy.md#publish-from-a-version-tag).
Backup/restore rehearsals and the opt-in runtime/provider checks below are separate qualification work; run the affected checks when changing those boundaries and before claiming release readiness.

## .NET suites

All three test projects use xUnit.net v3 and Microsoft Testing Platform (`xunit.v3.mtp-v2`). Run `dotnet test` from the repository root; do not add VSTest or xUnit v2 packages.

| Project | Evidence |
| --- | --- |
| ShutterSpool.Core.Tests | Pure Domain policies and Application workflows with controlled port substitutes. |
| ShutterSpool.IntegrationTests | Real PostgreSQL, filesystem, adapter, recovery, concurrency and authorized HTTP behavior. |
| ShutterSpool.ArchitectureTests | Project/assembly dependency direction, internal persistence, composition-only Infrastructure access and explicit HTTP DTOs. |

Set `SHUTTERSPOOL_INGEST_TEST_CONNECTION` to a controlled PostgreSQL admin connection. PostgreSQL tests create and drop only randomly named test databases; use synthetic media and isolated temporary roots. CI provisions PostgreSQL and sets `SHUTTERSPOOL_REQUIRE_POSTGRES_TESTS=true`; a missing connection fails that required job. Local runs without a connection explicitly skip PostgreSQL cases and do not provide database acceptance evidence.

Run one affected project with `dotnet test --project tests/ShutterSpool.Core.Tests/ShutterSpool.Core.Tests.csproj --no-restore` (substitute the other project as needed). After a full solution build, use `dotnet test --solution ShutterSpool.slnx --no-build`.

For a shared controlled PostgreSQL/provider host, bound collection concurrency with the built runner: `dotnet tests/ShutterSpool.IntegrationTests/bin/Debug/net10.0/ShutterSpool.IntegrationTests.dll -maxThreads 2`. This retains every test and each test's explicit concurrent workers/transactions; the default collection count follows host CPU threads.

Unix permission checks, Windows symbolic-link privileges, the distinct Linux `/dev/shm` storage-mount check, and `SHUTTERSPOOL_LOCAL_CAPACITY_TEST_ROOT` tiny-filesystem fault checks are separately identified. Synthetic checks do not establish physical-camera or external-provider compatibility.

After starting the local Keycloak companion through Aspire, run `pwsh -File tests/verify-keycloak-bootstrap.ps1 -KeycloakContainer <running-keycloak-container-name>` from the repository root.
This runtime check executes the mounted reconciliation script in uniquely named disposable realms. It verifies fresh subject retention, exact username matching, repeated reconciliation and preservation of identity/credentials on a rejected subject mismatch.
It also checks fresh-realm provisioning with optional email/name fields and no SMTP, then verifies that reconciliation preserves operator-managed profile/realm verification policy and account enabled, verification and MFA-action controls. The dedicated enrollment service client and its native user-management grant are checked separately from application workspace permissions.
It deletes only its own realms and temporary files; existing realms and deployment secrets are retained.
Successful password and PKCE login need separate runtime qualification. PostgreSQL CI acceptance remains mandatory independently of this companion check.

The database suite checks tenant/control migration models, fresh provisioning/restart, writer exclusion, database/storage identity, key custody and Collection constraints, and non-destructive refusal of incompatible histories and unversioned objects.
It does not qualify conversion of older installation layouts or native filesystem allocation enforcement.
Role checks cover permission/visibility boundaries and atomic membership, Owner, policy and setup audit changes.
Invitation checks cover exact verified-recipient acceptance, tenant isolation, local offer/membership/audit consistency, revisions, retries and recipient removal. Native enrollment adapter checks use controlled HTTP responses; they cannot establish real Keycloak policy enforcement, setup-link behavior or SMTP/mailbox delivery.

Preserve meaningful regression expectations when changing implementation boundaries. Tests supply evidence for the request and documented safety constraints, rather than replacing them.

## Frontend checks

From `src/ShutterSpool.Web`, run `npm ci`, `npm run typecheck`, `npm test` and `npm run build` as described in the [Web guide](../src/ShutterSpool.Web/README.md#build-and-check).
The Vitest suite covers browser state, autosave, reconciliation, private preview handling, Collections, retention presentation and interaction policies.
Mounted Nuxt tests exercise rendering and interactions with synthetic DOM events.
Use a real browser to verify layout, focus, keyboard and touch behavior, and visible failure states.

## Controlled Nextcloud delivery checks

The opt-in `Nextcloud_*` integration checks use synthetic media, a controlled PostgreSQL connection and an explicitly disposable HTTPS Nextcloud instance.
Set `SHUTTERSPOOL_NEXTCLOUD_PROFILE` to a private JSON file containing `url` (existing user collection), `username` and `password`; keep it outside Git and do not print it.
Set `SHUTTERSPOOL_NEXTCLOUD_CONTAINER` to the disposable `m201-*` Docker container name to enable outage checks. They stop/restart only that container, recreate worker contexts, and crash/restart an owned Server subprocess against retained PostgreSQL/spool/key state with Valkey unavailable.
The provider must retain its data across stop/start; use a dedicated disposable volume, not tmpfs.
TLS must validate normally through the deployment trust store; these tests do not bypass certificate or hostname checks.
Run `dotnet test --project tests/ShutterSpool.IntegrationTests/ShutterSpool.IntegrationTests.csproj --no-build --filter-method '*Nextcloud*'` after building.
The tests retain uniquely named synthetic provider collections for independent inspection; remove the disposable provider volume when the acceptance run ends.
They cover nested paths, RAW/JPEG names, a 16 MiB non-seekable original, server-computed upload SHA-256, normal acknowledgement and lost-MOVE checksum recovery without media download, independent final inspection, occupied-content preservation, durable outage backoff, local confirmation and identity locking.
See the [tested WebDAV profile](../src/ShutterSpool.Infrastructure/README.md#tested-webdav-profile) for provider, proxy, storage and payload limits.
These checks do not establish compatibility with every WebDAV server or power-loss durability.

## Public Python and shell checks

Run checks from the repository root with Python 3.10+ unless noted. These use controlled synthetic fixtures; they do not establish physical-camera, production-network or power-loss compatibility.

| Check | Purpose and prerequisites |
| --- | --- |
| `python tests/verify-camera-ftp-client.py` | Exercise the shared `tests/camera_ftp.py` client's certificate/hostname validation, protected-data and no-plaintext-fallback behavior. |
| `python tests/verify-edge-startup.py` | Check that the packaged edge entrypoint accepts a valid supplied certificate and refuses missing or expired certificates, hostname mismatches and mismatched private keys. Uses an SFTPGo stub and requires a POSIX shell and OpenSSL. |
| `python tests/verify-release-build.py` | Check Compose package construction and refusal paths with controlled fixtures. |
| `python tests/verify-ftps-edge.py` | Exercise the pinned disposable edge's protocol behavior and controlled certificate replacement. Requires OpenSSL and a Docker setup that lets the container reach the fixture callback at `host.docker.internal`, as Docker Desktop does. Add `--legacy` for the separate RSA AES-CBC profile. |
| `python tests/verify-keycloak-theme.py` | Check native enrollment defaults/verified-email mapper and theme preflight, selection and fallback; shell cases use the pinned Keycloak image and a controlled administrative-command stub. Requires Docker; this optional check does not prove email delivery or account enrollment. |
| `python tests/verify-sftpgo-httpfs.py` | Characterize synchronous completion, same-name uploads, rejected append/resume and hook-failure cleanup with synthetic bytes on a disposable loopback FTP endpoint; requires Docker. |

`camera_ftp.py` is the reusable synthetic client, not a standalone acceptance run. It keeps the socket address separate from the TLS hostname and never falls back to plaintext after TLS failure.

In the pinned FTPS fixture, a protected upload completes and the session closes before the edge restarts with a second valid synthetic certificate.
A fresh FTPS client validates the chain and hostname and checks that the served certificate fingerprint matches the replacement.
It does not exercise ACME issuance, unattended renewal or a physical camera's trust store.
The SFTPGo check removes its own uniquely named container and temporary callback listener; it characterizes the dependency rather than proving ShutterSpool's end-to-end durability.

For package construction see [Compose package building](../src/ShutterSpool.AppHost/README.md#build-a-compose-package).
For an actual stopped-writer backup/restore use the [operator maintenance guide](../deploy/operations/README.md).
Check explicit skips and the relevant runtime evidence before making an installation, recovery or compatibility claim.
