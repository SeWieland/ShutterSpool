# Test guidance

Choose a check by the claim you need to prove: a pure policy decision, a real persistence/recovery boundary, or an architectural dependency.
Fast substitutes are useful for workflows, but they cannot establish a filesystem or PostgreSQL durability guarantee.

## CI frequency

Pull requests targeting `main` also run `Dependency review`, which rejects newly introduced dependencies with known high or critical vulnerabilities, including development dependencies.
Dependabot checks GitHub Actions, npm and NuGet weekly and proposes security updates; updates still require the normal checks and maintainer review.
GitHub-managed CodeQL scans Actions, C#, JavaScript/TypeScript and Python on supported pushes and pull requests and on its scheduled runs. Static analysis complements the checks below; it does not qualify camera or recovery behavior.

Pushes to `main` and pull requests targeting `main` run the .NET suites with PostgreSQL, frontend type/test/build checks, migration and generated-client consistency checks, camera-client security, edge certificate validation and lightweight release-builder tests.
The release-builder tests use temporary fixtures and do not build or upload candidate bundles.
Full image/Compose candidate builds are manual through the [Candidate artifacts workflow](../.github/workflows/candidate.yml), with an explicit prerelease version and exact commit SHA.
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

After starting the local Keycloak companion through Aspire, run `pwsh -File tests/verify-keycloak-bootstrap.ps1 -KeycloakContainer <running-keycloak-container-name>` from the repository root. This separate runtime check uses the mounted reconciliation script and a uniquely named disposable realm to verify fresh subject retention, exact username matching, repeated reconciliation and preservation of identity/credentials on a rejected subject mismatch. It deletes only the realm and temporary files created by that invocation; existing realms and deployment secrets are retained. PostgreSQL CI acceptance remains mandatory independently of this companion check.

The database suite checks the single initial migration, model/snapshot consistency, fresh startup/restart, concurrent startup serialization, SQL-owned key custody and Collection constraints, and non-destructive refusal of historical histories and unversioned objects. It does not exercise upgrades from historical migration chains.

Preserve meaningful regression expectations when changing implementation boundaries. Tests supply evidence for the request and documented safety constraints, rather than replacing them.

## Frontend checks

From `src/ShutterSpool.Web`, run `npm ci`, `npm run typecheck`, `npm test` and `npm run build` as described in the [Web guide](../src/ShutterSpool.Web/README.md#build-and-check).
The native Node checks cover browser state, autosave, reconciliation, private preview handling, Collections, retention presentation and interaction policies.
Use browser inspection for actual layout, focus, keyboard/touch behavior and visible failure states; Node checks alone do not establish usability.

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
| `python tests/verify-edge-startup.py` | Reject absent, expired, wrong-host or wrong-key edge certificates; requires a POSIX shell and OpenSSL. |
| `python tests/verify-release-build.py` | Check package construction and refusal paths with controlled fixtures. |
| `python tests/verify-ftps-edge.py` | Exercise the pinned disposable edge's protocol behavior and controlled certificate replacement; requires Docker and OpenSSL. Add `--legacy` for the separate RSA AES-CBC profile. |
| `python tests/verify-keycloak-theme.py` | Check theme preflight, selection and fallback in the pinned Keycloak shell with a controlled administrative-command stub; requires Docker. |
| `python tests/verify-sftpgo-httpfs.py` | Characterize synchronous completion, same-name uploads, rejected append/resume and hook-failure cleanup with synthetic bytes on a disposable loopback FTP endpoint; requires Docker. |

`camera_ftp.py` is the reusable synthetic client, not a standalone acceptance run. It keeps the socket address separate from the TLS hostname and never falls back to plaintext after TLS failure.
The FTPS edge check validates a synthetic replacement certificate's chain, hostname and key, rejects an invalid candidate without changing the served certificate, then verifies a controlled restart serves the replacement.
It does not exercise ACME issuance, unattended renewal or a physical camera's trust store.
The SFTPGo check removes its own uniquely named container and temporary callback listener; it characterizes the dependency rather than proving ShutterSpool's end-to-end durability.

For package construction see [Compose candidate building](../src/ShutterSpool.AppHost/README.md#prepare-a-compose-candidate).
For an actual stopped-writer backup/restore use the [operator maintenance guide](../deploy/operations/README.md).
Check explicit skips and the relevant runtime evidence before making an installation, recovery or compatibility claim.
