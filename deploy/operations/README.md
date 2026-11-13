# Provision, maintain and recover an installation

Use a maintenance window and standard Docker, PostgreSQL and archive tools.
Commands run from the extracted package directory on Linux/WSL2 with the installation's complete `COMPOSE_FILE` selection.
Use a privileged operator shell where file ownership requires it, and `umask 077` for backup work.
Never use `down -v`, volume pruning or a fresh database with an old spool to repair startup.

Examples below cover bundled PostgreSQL and Keycloak.
External-service owners provide equivalent database/role and identity custody during the same application shutdown interval.
Do not stop a shared external IdP or dump unrelated databases: preserve that issuer under its provider's recovery procedure.
Valkey and previews are disposable; destination storage has separate custody.

Every installation has a control database and one or more workspace databases and storage allocations.
Back up all of them, matching keys/configuration and required identity state in the same stopped-writer interval.
Keep each dump and storage copy identified by workspace name; preserve its internal identity markers, restricted roles and allocation boundary.
Do not restore one workspace independently or combine registry and tenant state from different recovery points.

## Workspace provisioning

One Server serves up to 32 workspaces. A personal installation follows the same procedure with one workspace.
The operator chooses unique names such as `studio`; ShutterSpool generates the internal workspace and storage IDs.
There is no adoption of an older installation. Keep existing databases, media and keys intact when creating fresh state.

### Prepare the boundaries

Keep Server and camera edges stopped while preparing:

1. An empty control database and an empty database per workspace, each with a migration owner and a separate restricted runtime login. Runtime logins must not own databases/schemas, create objects, inherit other roles or hold privileged PostgreSQL attributes.
2. Database connection grants limited to the intended roles, with `PUBLIC` access revoked. Tenant roles must have no access to the control database or another tenant. The provisioning command grants required schema/table access inside its selected database.
3. An empty, filesystem-enforced allocation per workspace. Use bounded volumes or native filesystem quotas; a directory or bind mount alone does not enforce a budget. Total allocations must fit usable capacity after reserves and other host data.
4. The first Owner's verified OIDC issuer and subject, plus the identity of the trusted operator performing setup. Deployment administration alone grants no workspace membership.

For the bundled PostgreSQL example, `package/workspace-databases.sql` creates `shutterspool_control` and `shutterspool_studio`, the migration owner and the two runtime roles.
Supply `CONTROL_RUNTIME_PASSWORD`, `STUDIO_RUNTIME_PASSWORD` and `MIGRATION_PASSWORD` as exported environment variables through your private secret-management process; do not place their values in command history.
Start only PostgreSQL, then prepare the databases:

```sh
docker compose up --detach --wait postgres
docker compose exec -T -e CONTROL_RUNTIME_PASSWORD -e STUDIO_RUNTIME_PASSWORD -e MIGRATION_PASSWORD postgres \
  sh -c 'exec psql -U "$POSTGRES_USER" -d postgres' < package/workspace-databases.sql
unset CONTROL_RUNTIME_PASSWORD STUDIO_RUNTIME_PASSWORD MIGRATION_PASSWORD
```

The script preserves existing passwords and databases. Its example names must match the connection strings in the secret JSON files below.
Use normal PostgreSQL administration for additional workspaces or an external database server.

### Configure once

The package uses standard .NET configuration with three files:

| File under `config/` | Contents | Consumers |
| --- | --- | --- |
| `appsettings.json` | Shared defaults and workspace definitions: allocations, paths and initial identities. | Server and maintenance |
| `appsettings.Production.json` | Runtime connection strings, application secrets and production-only settings. | Server only |
| `appsettings.Maintenance.json` | Runtime connections needed for validation, migration connections and the retained encryption key. | Maintenance only |

Edit the supplied files for your installation. Server selects Production; the operator container selects Maintenance.
.NET loads `appsettings.json` and the selected environment file automatically, with normal environment-variable/command-line overrides.
There are no custom file readers or configuration-loading hooks.
Workspace definitions exist once in the shared base. Keep runtime credentials and the encryption key consistent where both environment files need them.

The `studio` definition in `appsettings.json` contains its allocation, initial Owner and setup attribution:

```json
"Workspaces": {
  "UsableCapacityBytes": 107374182400,
  "Definitions": {
    "studio": {
      "StorageRoot": "/srv/shutterspool/studio",
      "AllocatedBytes": 107374182400,
      "OwnerIssuer": "https://identity.example.invalid/realms/shutterspool",
      "OwnerSubject": "the-verified-subject",
      "SetupOperatorIdentity": "the-verified-operator"
    }
  }
}
```

This section belongs inside `ShutterSpool`. `UsableCapacityBytes` is the deployment total after reserves; `AllocatedBytes` is this workspace's fixed share.
All connection strings belong under `ConnectionStrings`. The Production file uses:

```json
"ConnectionStrings": {
  "shutterspool-control": "Host=postgres;Database=shutterspool_control;Username=shutterspool_control_runtime;Password=...",
  "Workspaces": {
    "studio": {
      "Runtime": "Host=postgres;Database=shutterspool_studio;Username=shutterspool_studio_runtime;Password=..."
    }
  }
}
```

The Maintenance file includes those runtime entries and adds `ConnectionStrings:shutterspool-control-migration` and `ConnectionStrings:Workspaces:studio:Migration`, using the migration owner for those same databases.
The workspace name is the key in both sections; no connection alias or manually supplied workspace ID is needed.
Use distinct runtime passwords for the control and each workspace role. The control registry stores provisioning/allocation facts, never connection passwords or authoritative membership grants.

Native Compose configs mount the non-secret base at `/app/appsettings.json`; secrets mount each environment file at its expected `/app/appsettings.*.json` path.
Normal Server never receives the Maintenance file. Keep credentials out of the shared base.
The image runs as UID/GID 1654; protect secret JSON files for that consumer. Compose provides per-service access, not encryption of host files.

To add another workspace, add its shared definition and environment connection entries, then mount its allocation in both services in `components/workspaces.yaml`.
Keep `StorageRoot` equal to the container mount target. Host paths resolve from the package's base Compose file.
A bind mount exposes the allocation; it does not create a filesystem quota. Give UID/GID 1654 write/traverse access to the root.

Set `ShutterSpool:Destinations:EnabledKinds` to the intended subset of `local` and `webdav`; none are enabled by default.
Local delivery uses the operator-created `destinations` directory inside the workspace allocation.
WebDAV denies private/local and special-use addresses unless the operator permits the required CIDR through `ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks`.
Certificate validation and no-redirect behavior remain required.
Choose listeners through [camera profiles](../README.md#camera-profiles); apply capability changes during stopped-writer maintenance.

### Initialize and provision

Validate Compose, start the required database service, then initialize the registry and named workspace:

```sh
set -eu
docker compose config --quiet
docker compose up --detach --wait postgres
docker compose run --rm --no-deps maintenance --workspace-control-init
docker compose run --rm --no-deps maintenance --workspace-provision studio
```

For external PostgreSQL, use its normal service procedure instead of starting `postgres`.
One-off maintenance containers publish no service ports and exit without starting HTTP or workers.
Only start the normal graph after every workspace provisions successfully.

Provisioning refuses unknown nonempty databases/roots, creates schema, keys, defaults and identity markers, then activates the workspace.
The initial Owner grant and its operator-attributed audit/result commit together; an audit failure leaves no active initial grant.
Set `ShutterSpool:Workspaces:Definitions:<name>:SetupOperatorIdentity` before unfinished initial setup can commit. This is a trusted-host attestation, not application authentication of the operator; keep verification evidence in the protected onboarding record.
Retry the same name and inputs after interruption; changed initial targets or member identity are a conflict.
Repeating a completed provision never regrants removed membership. Do not erase registry rows, markers or migration history to force a retry.
Completed provisioning and upgrades do not require a new setup attestation or fabricate historical operator evidence.

Startup validates database identity, privileges, exact schema, keys and storage markers, then completes recovery.
Resolved mappings remain fixed until restart. Startup does not migrate schemas or replay the initial grant.
Moving a validated database/root during recovery requires stopped writers and restart; persistent tenant/storage identities must still match.

### Upgrade databases

After [stopping writers](#stop-writers-before-capture) and capturing an eligible complete backup:

```sh
docker compose run --rm --no-deps maintenance --workspace-upgrade
```

Server holds `.shutterspool-writers.lock` in each root for its lifetime; maintenance must acquire those same leases.
Operator changes serialize through the control database, while EF/Npgsql handles migration locking.
These safeguards do not stop unrelated tools. Keep the instance closed after any failed upgrade, identity, key or recovery check.
Repair forward or restore a complete eligible set under the [recovery boundary](#supported-recovery-boundary).
The workspace-role migration preserves existing grants and their active/inactive state, assigning them Owner authority to retain their prior administrative access. It starts Viewer visibility at released, non-rejected Active Captures and preserves existing media and workflow state.
Review current Owners after upgrading. The migration deliberately refuses a downgrade that would discard role or access-audit state; repair forward or restore a complete eligible recovery set.

### Resize an allocation

Stop writers, adjust and qualify the filesystem boundary, and change the definition's `AllocatedBytes` to the new value.
Then record the change, supplying the expected previous value:

```sh
# Example: grow studio from 2 GiB to 4 GiB.
docker compose run --rm --no-deps maintenance --workspace-allocation studio 2147483648 4294967296
```

The command checks the previous allocation, total capacity, saved workspace quota and retained bytes/reservations; an already-applied retry succeeds.
Lower the workspace quota through its normal impact-confirmed settings flow before shrinking, and never shrink a filesystem below retained data.
The command records a budget; it does not resize storage or delete media.

Workspace quotas cannot exceed their allocation or disable its cap. Receive, original, quarantine, temporary-copy and preview writes stay within that boundary.
Unused allocation cannot be borrowed; physical free-space checks still apply.
Application tests do not establish filesystem enforcement, power-loss recovery or hosted readiness; qualify those on the selected platform.

## Owner login recovery

Recover access to the same existing Owner identity through the identity provider's native self-service recovery or trusted administration. Preserve its exact issuer and subject so the existing local membership still applies. If another Owner can sign in, that Owner can use **Settings → Members** to manage existing memberships, with separate confirmation for Owner promotion and protection for the last active Owner.

Native recovery depends on the provider and configured account factors; email recovery requires SMTP. The operator is responsible for trusted identity administration and its attribution. An identity-provider administrator or realm role alone grants no local workspace Owner authority. Application audit records the authenticated principal; it cannot establish which operator used provider impersonation or another identity-administration facility.

If no existing Owner identity can be recovered, a trusted application/database operator may repair the workspace grant manually as exceptional maintenance. This requires privileged access and responsibility for the exact principal, workspace, remaining Owners and current authorization state. ShutterSpool supplies no custom recovery command, request, representative/contact registry or notification workflow.

Initial Owner provisioning remains explicit, locally audited and independent of SMTP. Ordinary restart cannot take over a workspace or replay its initial grant. Keep a current authorization checkpoint outside backups and reconcile current memberships and credentials before reopening restored state; an older database alone cannot establish current authority.

## Supported recovery boundary

This package supports recovery of a complete consistent backup when no privacy erasure has occurred since its recovery point.
Recovery from a backup predating erasure, or with uncertain erasure history, is unsupported.
Existing suppression import/startup safeguards remain; never disable them to force a restore.
Ordinary startup cannot detect arbitrary database rollback or prove supplied suppression evidence is newest.

Preserve the matching application database, entire spool/recovery storage, encryption key, configuration and required identity state together.
Do not regenerate missing keys. A successful dump is not restore proof.
Acknowledgements and edits after the recovery point are absent; restoring cannot undo remote deliveries.

## Stop writers before capture

1. Block new management traffic at the operator's proxy. Stop bundled `proxy` when selected.
2. Signal every enabled edge with SIGINT while Server remains available. Repeat for legacy/plain edges when enabled:

   ```sh
   docker compose kill --signal SIGINT camera-edge
   timeout 180 docker wait "$(docker compose ps --all --quiet camera-edge)"
   ```

   Inspect exit statuses and bounded logs. Timeout or abnormal termination aborts this capture attempt.
   Preserve custody and investigate; do not force-kill an unfinished transfer or claim a clean drain after the edge's 300-second grace limit cancels it.
3. Stop Server and bundled identity writers, omitting services absent from the selected graph:

   ```sh
   docker compose stop --timeout 150 server
   docker compose stop --timeout 150 keycloak keycloak-reconciler
   docker compose ps --all
   ```

   Verify normal exits and no other writers. Server's shutdown budget is 120 seconds.
   A force-killed writer needs private restart/recovery and another clean drain before capture.
4. Keep PostgreSQL running for logical dumps. Keep configuration and certificates unchanged during capture.

## Capture a complete backup

Choose a new private directory outside the installation with room for a complete spool copy.
Run with sufficient privileges to preserve numeric owners. Stop on every failed command:

```sh
set -eu
umask 077
backup=/secure/backups/shutterspool-2026-10-21
mkdir -m 0700 "$backup"
server_id=$(docker compose ps --all --quiet server)
test -n "$server_id"
docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -Fc shutterspool_control' > "$backup/control.dump"
docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -Fc shutterspool_studio' > "$backup/studio.dump"
docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -Fc keycloak' > "$backup/keycloak.dump"
docker compose exec -T postgres sh -c 'exec pg_dumpall -U "$POSTGRES_USER" --globals-only' > "$backup/roles.sql"
mkdir "$backup/studio"
docker cp -a "$server_id:/srv/shutterspool/studio/." "$backup/studio/"
tar -czpf "$backup/configuration.tar.gz" .env config secrets certificates compose.yaml profiles components proxies package
cp release-manifest.json "$backup/"
docker compose exec -T postgres pg_restore --list < "$backup/control.dump" > "$backup/control.contents"
docker compose exec -T postgres pg_restore --list < "$backup/studio.dump" > "$backup/studio.contents"
docker compose exec -T postgres pg_restore --list < "$backup/keycloak.dump" > "$backup/keycloak.contents"
tar -tzf "$backup/configuration.tar.gz" > "$backup/configuration.contents"
```

Adjust database names; omit the identity dump for an independently managed issuer.
This example covers the control database and one workspace. Repeat the workspace dump/list verification and root copy for every additional definition; the control database has no media root.
Use distinct dump and directory names so no workspace copy overwrites another. Include the fixed allocation configuration and native volume/quota setup in the recovery inventory.
Include all additional Compose overrides, external secret/certificate mounts and local destination mount configuration.
Inspect the complete copied spool/recovery inventory and numeric ownership.
Record the stopped-writer interval, package and database versions in an operator note; retain matching image/package artifacts.
The release manifest identifies artifacts, not backup completeness.

The archive and roles dump contain secrets. Encrypt the completed set under operator custody and keep recovery keys separately accessible.
Time and space grow with retained originals; a backup on the same failed disk provides no independent recovery.
After checking every capture, restart the selected graph and verify health and authenticated media access:

```sh
docker compose up --detach --wait --wait-timeout 240
docker compose exec -T server dotnet ShutterSpool.Server.dll --health-check
```

Label a failed capture incomplete. Inspect service states before deliberately resuming; preserve partial backup and original volumes.

## Restore into an isolated candidate

Use the same compatible package/image, a new directory/project name and empty volumes.
Keep the original installation and backup intact. Restore configuration privately, preserving ownership, then change `COMPOSE_PROJECT_NAME` before any Compose command.
Before any Compose command, choose distinct free `BACKEND_SUBNET`, `PROXY_SUBNET` and `IDENTITY_PROXY_SUBNET` values: the stopped original installation still owns its networks.
Supply isolated external databases where applicable; do not retain old explicit volume names or database targets.
Keep proxy and camera edges stopped throughout validation.

Before creating, starting or copying into any candidate container, prepare a fresh qualified empty host allocation for **every workspace**.
Changing the Compose project name does not change absolute bind sources: the restored `components/workspaces.yaml` still points at the original host roots.
Change each workspace's bind `source` in **both Server and maintenance**, including any additional overrides. Retain the configuration and secret mounts.
For example, after preparing a separate bounded allocation at `/srv/shutterspool-restore/studio`, change both allocation entries to:

```yaml
services:
  server:
    volumes:
      - type: bind
        source: /srv/shutterspool-restore/studio
        target: /srv/shutterspool/studio
        bind:
          create_host_path: false
  maintenance:
    volumes:
      - type: bind
        source: /srv/shutterspool-restore/studio
        target: /srv/shutterspool/studio
        bind:
          create_host_path: false
```

The container target can remain unchanged; otherwise the shared workspace `StorageRoot` must match the new target.
Inspect the fully resolved mounts privately with `docker compose --profile maintenance config` before creating containers.
Resolve every original and candidate host allocation with `realpath` and confirm the canonical paths are disjoint: none may equal, contain or be contained by an original allocation or another workspace's allocation.
Do not use a symlink or bind alias to an original allocation. Verify each candidate allocation is empty, has its qualified limit and grants UID/GID 1654 the required access.
Do not continue if any mount still reaches original storage; copying a backup into that container would overwrite the retained installation.

Start only PostgreSQL. Review the roles dump and recreate the required roles and empty databases before restoring; use the example setup script only where its names match.
Never replay cluster-wide role changes blindly into shared PostgreSQL. For the example layout:

```sh
docker compose up --detach --wait postgres
docker compose exec -T postgres sh -c 'exec pg_restore -U "$POSTGRES_USER" --exit-on-error --dbname shutterspool_control' < "$backup/control.dump"
docker compose exec -T postgres sh -c 'exec pg_restore -U "$POSTGRES_USER" --exit-on-error --dbname shutterspool_studio' < "$backup/studio.dump"
docker compose exec -T postgres sh -c 'exec pg_restore -U "$POSTGRES_USER" --exit-on-error --dbname keycloak' < "$backup/keycloak.dump"
docker compose up --no-start --no-deps server
restore_server_id=$(docker compose ps --all --quiet server)
test -n "$restore_server_id"
docker cp -a "$backup/studio/." "$restore_server_id:/srv/shutterspool/studio/"
```

Restore every additional workspace database/root during the same interval. Reestablish native allocation limits before startup.
Any failed restore leaves the candidate closed.
Compare complete spool inventory and representative original hashes with the backup, including numeric ownership.

Isolate delivery destinations or preserve their original storage identity and account for at-least-once retries before starting Server.
Server resumes workers after recovery; unpublished HTTP ports do not suppress outbound delivery.
Start only selected private dependencies and Server, without starting the proxy/edges:

```sh
docker compose up --detach --no-deps keycloak
docker compose up --detach --no-deps server
docker compose exec -T server dotnet ShutterSpool.Server.dll --health-check
```

Allow startup to finish before retrying health; inspect logs on failure without weakening validation.
Verify Source/Capture identities, curation, original hashes and delivery outcomes using a controlled private client.
Open traffic only after validation and deliberate operator cutover. Stop isolated validation candidates and preserve their volumes afterward.

## Upgrade and rollback

Use only the release's explicitly named compatible predecessor.
Drain, capture a verified recovery set, replace package/image/configuration and complete migrations/recovery before reopening traffic.
Use the explicit [workspace upgrade command](#upgrade-databases) with operator migration credentials; ordinary startup never migrates tenant databases.
Keep the whole instance closed until all registered databases, storage identities, keys, allocation limits and recovery checks pass. A maintenance page alone does not stop writers.
Never run old and new Servers concurrently against one database/spool.
Failed initialization leaves the installation closed; preserve diagnostics and the pre-upgrade set.

Rollback restores complete compatible pre-upgrade state into isolated storage; it is not a schema downgrade or image-tag change.
The same erasure-history restrictions apply. Explain recovery-point loss and completed remote deliveries before cutover.

## Moving from an older pre-alpha installation

There is no conversion of older layouts. Use new empty control/workspace databases and allocated roots; recreate Sources and update every device sharing their credentials.
Preserve the old databases, storage, keys, configuration and identity-provider state together.
Never edit migration history or erase existing state to force initialization.
The previous application can reopen only its original retained state; it cannot read newly ingested data from the new installation.
