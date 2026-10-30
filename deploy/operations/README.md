# Provision, maintain and recover an installation

Use a maintenance window and standard Docker, PostgreSQL and archive tools.
Commands run from the extracted package directory on Linux/WSL2 with the installation's complete `COMPOSE_FILE` selection.
Use a privileged operator shell where file ownership requires it, and `umask 077` for backup work.
Never use `down -v`, volume pruning or a fresh database with an old spool to repair startup.

Examples below cover bundled PostgreSQL and Keycloak.
External-service owners provide equivalent database/role and identity custody during the same application shutdown interval.
Do not stop a shared external IdP or dump unrelated databases: preserve that issuer under its provider's recovery procedure.
Valkey and previews are disposable; destination storage has separate custody.

For shared-workspace mode, first follow the [workspace provisioning procedure](#shared-workspace-provisioning).
The single-database capture/restore examples below are not a complete backup of a shared instance: include the control database, every registered tenant database, every allocated storage root, matching keys/configuration and required identity state in the same stopped-writer interval.
Keep each database dump and storage root identified by its workspace and storage identity; preserve restricted runtime/migration roles and filesystem allocation boundaries on restore.
Do not restore one tenant independently or restore current registry state beside older tenant state.

## Shared workspace provisioning

Shared-workspace mode serves up to 32 workspaces from one Server, with a separate PostgreSQL database, restricted runtime role and fixed filesystem allocation for each.
It is an explicit deployment choice: set `ShutterSpool:Workspaces:Enabled=true` before provisioning.
Existing single-workspace data is not adopted or reset. Retain its database, spool and matching keys/configuration when creating a separate installation.

Prepare storage and PostgreSQL with standard operator tools while Server and camera edges are stopped:

1. Create an empty control database and an empty database for each workspace, with separate migration owners and restricted runtime logins. Runtime logins must not own their database/schema, inherit other roles, create objects or hold privileged PostgreSQL attributes.
2. Revoke `PUBLIC` connection access to these databases; grant connection access only to their intended migration/runtime roles. Do not grant tenant roles access to another tenant or the control database. The CLI grants the needed table/schema privileges within its selected database.
3. Provision an existing empty, filesystem-enforced storage allocation for each workspace. Use bounded volumes or native filesystem quotas; an ordinary directory or thin-provisioned promise alone is insufficient. Total allocations must fit usable storage after physical reserves and non-workspace overhead.
4. Establish the first member's verified OIDC issuer and subject from an account they can sign into. An operator identity is not automatically a workspace member.

The following settings use standard configuration notation; environment variables replace `:` with `__`.
Connection references name entries in `ConnectionStrings`, not literal connection strings supplied by a client.
Any named connection can use a `<reference>File` entry pointing to a mounted secret file instead of an inline connection string.

| Setting | Purpose |
| --- | --- |
| `ShutterSpool:Workspaces:UsableCapacityBytes` | Operator's usable total, after reserves and storage needed outside workspace allocations. |
| `ConnectionStrings:shutterspool-control` | Restricted control registry/routing connection for normal Server operation. |
| `ConnectionStrings:shutterspool-control-migration` | Separate privileged connection to that same control database, only for operator commands. |
| `ShutterSpool:Workspaces:Definitions:<workspace-guid>:Name` | Workspace label. |
| `…:StorageIdentity`, `…:StorageRoot`, `…:AllocatedBytes` | Stable operator storage identity, absolute allocated root and fixed byte budget. |
| `…:ConnectionReference`, `…:MigrationConnectionReference` | Runtime and migration connection names for the same tenant database, with distinct roles. |
| `…:OwnerIssuer`, `…:OwnerSubject` | Verified initial member identity bound to the configured OIDC authority. HTTPS is required except for the supported non-packaged loopback development issuer. |

Definitions and migration connections are operator-command inputs; normal Server operation reads activated definitions from the control registry and needs only its restricted control/tenant connections.
Keep migration secrets out of the normal runtime's mounts/configuration. Preserve the existing deployment encryption key with all tenant recovery sets.
The control registry stores routing/provisioning facts and connection reference names, not connection secrets or authoritative memberships.
This tenancy foundation gives provisioned members management access; delegated member roles are not yet available.

### Mount runtime and operator configuration

The package's default Compose graph is single-workspace configuration. Before starting shared mode:

1. In the generated core's `server` service, remove the `ConnectionStrings__shutterspoolFile` environment entry and its `database-connection` service secret. Retain the encryption-key secret and other required service configuration. Do not give normal Server operation a privileged legacy database connection.
2. Add the restricted control and tenant `<reference>File` settings to `config/server.json`, enable workspace mode and set usable capacity. For example, `ConnectionStrings:shutterspool-controlFile` can point to `/run/workspace-connections/control` and `ConnectionStrings:workspace-oneFile` to `/run/workspace-connections/workspace-one`.
3. Put only those restricted runtime connection files in `secrets/workspace-runtime`. Each contains its complete Npgsql connection string, using the database's address reachable from the Compose `backend` network (`postgres` for the bundled service). Keep privileged migration connections separately in `secrets/workspace-migration`.
4. Add a local override such as `config/workspaces.yaml` to the installation's `COMPOSE_FILE` selection. Mount every allocated root and the runtime connection directory into `server`; use the same container paths for operator commands. The example below adds one workspace. Substitute the already provisioned host allocation and repeat the root mount for each workspace.

```yaml
services:
  server:
    volumes:
      - type: bind
        source: ./secrets/workspace-runtime
        target: /run/workspace-connections
        read_only: true
        bind:
          create_host_path: false
      - type: bind
        source: /srv/shutterspool/workspace-one
        target: /var/lib/shutterspool/workspaces/11111111-1111-4111-8111-111111111111
        bind:
          create_host_path: false
```

Bind source paths resolve from the package's base Compose file, even when the override is under `config`.
Set that workspace's `StorageRoot` to the exact container target above; paths in the registry must match normal runtime and maintenance containers.
The bind mount exposes an allocation; it does not create or enforce its filesystem quota.
The image runs as UID/GID 1654. Give that identity write/traverse access to allocated roots and read/traverse access to its restricted connection files/directories; keep other host users out.
Shared mode enables no destination kinds by default. Set the operator-controlled `ShutterSpool:Destinations:EnabledKinds` array in `config/server.json` to the intended subset of `local` and `webdav`.
If local destinations are enabled, the operator creates their `destinations` directory inside the allocation before members configure them. WebDAV denies private/local and special-use addresses unless the operator permits the exact required CIDR through `ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks`; normal certificate validation and no-redirect behavior remain required.
Choose camera listeners through the [package camera profiles](../README.md#camera-profiles); apply capability changes only while writers are stopped. Enabling a type does not qualify its storage or provider.

Create a private `config/workspace-operator.json` containing the same workspace mode/capacity, OIDC security settings and runtime connection file references, plus every exact provisioning definition and migration `<reference>File` setting from the table above.
For example, `ConnectionStrings:shutterspool-control-migrationFile` can point to `/run/workspace-migration/control`, and the tenant's migration reference to `/run/workspace-migration/workspace-one`.
Keep the operator file and migration directory readable only by their operator/UID 1654 consumers. Never copy migration secrets into the runtime directory or mount them into the ordinary Server.
Preserve the verified initial Owner and original provisioning values in this private operator inventory for retries and upgrades.

Validate the complete Compose selection with `docker compose config --quiet`.
Start only the required database service if it is not already running; keep Server, camera edges and public traffic stopped. Use provider tools for external PostgreSQL.
From the package directory, define this host-shell function:

```sh
set -eu
workspace_operator() {
  docker compose run --rm --no-deps \
    --env ASPNETCORE_ENVIRONMENT=WorkspaceOperator \
    --volume "$PWD/config/workspace-operator.json:/app/appsettings.WorkspaceOperator.json:ro" \
    --volume "$PWD/secrets/workspace-migration:/run/workspace-migration:ro" \
    server "$@"
}
```

The one-off container uses the selected image's `dotnet ShutterSpool.Server.dll` entrypoint, inherited runtime mounts/network/encryption key and the additional operator-only configuration/secrets.
It publishes no service ports and the commands below exit without starting HTTP or workers. No shell or SDK is needed in the image.
### Initialize and provision

Stop on every failed command; do not start the normal graph until provisioning and all identity/key/schema/storage checks succeed:

```sh
workspace_operator --workspace-control-init
workspace_operator --workspace-provision 11111111-1111-4111-8111-111111111111 22222222-2222-4222-8222-222222222222
```

Replace both example GUIDs: the first identifies the configured workspace and the second identifies this provisioning operation. Retain them for retry.
Initialization refuses an unknown nonempty database/root. Provisioning records the exact targets and initial identity, establishes schema, keys, defaults and storage markers, and activates only after checks succeed.
Retry the same operation and inputs after interruption; changed targets/identity are a conflict. Repeating a completed operation does not regrant removed membership.
Do not erase registry rows, markers or migration history to force a retry.

### Upgrade workspace databases

Only after [stopping every writer](#stop-writers-before-capture) and capturing an eligible complete recovery set, set `ShutterSpool:Workspaces:WritersStopped=true` in `config/workspace-operator.json` for the operator process and run:

```sh
workspace_operator --workspace-upgrade
```

That flag is the operator's declaration, not a mechanism that stops other processes.
Normal Server operation holds `.shutterspool-writers.lock` in each allocated root for its lifetime; upgrade/allocation commands must acquire those same locks and refuse while the Server holds them.
Operator commands serialize through the control database. These safeguards do not stop unrelated tools or replace the stopped-writer procedure.
Keep the instance closed on any failed upgrade or identity/key/recovery check; repair forward or restore a complete eligible set under the [supported recovery boundary](#supported-recovery-boundary).
Normal startup validates exact schema compatibility and storage identity and completes tenant recovery without migrations or initial-grant replay.

### Resize a storage allocation

To resize an allocation, stop writers, adjust and requalify the native filesystem boundary, then use the same operator settings with `WritersStopped=true`:

```sh
# Example: change a 2 GiB allocation to 4 GiB; substitute the workspace and exact byte values.
workspace_operator --workspace-allocation 11111111-1111-4111-8111-111111111111 2147483648 4294967296
```

The command checks the expected current allocation, known tenant accounting and total deployment capacity; it refuses a size below retained occupancy/reservations or the saved workspace quota.
Lower that quota through its normal impact-confirmed settings flow before maintenance when shrinking. Never shrink a filesystem beneath retained data.
An already-applied retry is idempotent. The command changes only the assigned registry budget, not storage, keys, media or the original provisioning tuple; keep original definitions unchanged for provisioning retries.

Workspace quota settings cannot exceed the assigned allocation or disable its cap. Receive, original, quarantine, temporary-copy and disk-preview writes stay in that workspace's storage boundary.
Unused allocation cannot be borrowed from another workspace; fixed budgets do not guarantee that storage is currently available or replace physical free-space checks.
Platform enforcement and recovery must be qualified on the actual deployment; application tests are not persistent-volume or power-loss evidence.
This foundation does not provide delegated roles or invitations and does not establish readiness to open a hosted service.

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
docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -Fc shutterspool' > "$backup/shutterspool.dump"
docker compose exec -T postgres sh -c 'exec pg_dump -U "$POSTGRES_USER" -Fc keycloak' > "$backup/keycloak.dump"
docker compose exec -T postgres sh -c 'exec pg_dumpall -U "$POSTGRES_USER" --globals-only' > "$backup/roles.sql"
mkdir "$backup/spool"
docker cp -a "$server_id:/var/lib/shutterspool/spool/." "$backup/spool/"
tar -czpf "$backup/configuration.tar.gz" .env config secrets certificates compose.yaml profiles components proxies package
cp release-manifest.json "$backup/"
docker compose exec -T postgres pg_restore --list < "$backup/shutterspool.dump" > "$backup/shutterspool.contents"
docker compose exec -T postgres pg_restore --list < "$backup/keycloak.dump" > "$backup/keycloak.contents"
tar -tzf "$backup/configuration.tar.gz" > "$backup/configuration.contents"
```

Adjust database names; omit the identity dump for an independently managed issuer.
For a shared instance, repeat the application database dump/list verification and storage copy for the control database and each tenant/root from the operator inventory; the control database has no media root.
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

For the bundled layout, start only PostgreSQL and restore its empty application/identity databases:

```sh
docker compose up --detach --wait postgres
docker compose exec -T postgres sh -c 'exec pg_restore -U "$POSTGRES_USER" --exit-on-error --dbname shutterspool' < "$backup/shutterspool.dump"
docker compose exec -T postgres sh -c 'exec pg_restore -U "$POSTGRES_USER" --exit-on-error --dbname keycloak' < "$backup/keycloak.dump"
docker compose up --no-start --no-deps server
restore_server_id=$(docker compose ps --all --quiet server)
test -n "$restore_server_id"
docker cp -a "$backup/spool/." "$restore_server_id:/var/lib/shutterspool/spool/"
```

Review `roles.sql` and recreate required missing roles/grants before restoring; bundled bootstrap already creates its configured PostgreSQL role.
Never replay cluster-wide role changes blindly into shared PostgreSQL. Any failed restore leaves the candidate closed.
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
Shared-workspace mode requires the explicit [workspace upgrade command](#shared-workspace-provisioning) with operator migration credentials; ordinary startup never migrates tenant databases.
Keep the whole instance closed until all registered databases, storage identities, keys, allocation limits and recovery checks pass. A maintenance page alone does not stop writers.
Never run old and new Servers concurrently against one database/spool.
Failed initialization leaves the installation closed; preserve diagnostics and the pre-upgrade set.

Rollback restores complete compatible pre-upgrade state into isolated storage; it is not a schema downgrade or image-tag change.
The same erasure-history restrictions apply. Explain recovery-point loss and completed remote deliveries before cutover.

## Moving from an older pre-alpha installation

The initial migration `20261025211318_InitialCreate` requires a fresh application database and empty spool at separately selected locations when moving from an incompatible older installation.
Every historical migration chain is incompatible, including installations with Source connection profiles and Collections. There is no supported conversion of an older installation. Preserve its database, spool and original configuration/keys together; do not clear or reuse them for the new baseline.
Preserve shared PostgreSQL storage and identity-provider state as well.
Startup rejects an incompatible migration history before schema changes, key enrollment or recovery. Unversioned application tables, views and sequences also prevent fresh initialization. Do not edit or erase migration-history rows to bypass this guard, or use `EnsureCreated` to adopt a database.

Recreate Sources on the new baseline and update every device sharing their credentials.
Rollback uses the previous application with its retained database, spool and keys. Do not reopen the new baseline with the old application;
there is no downgrade conversion or backward transfer of newly ingested data.

This is a specific pre-alpha transition. Use [upgrade and rollback](#upgrade-and-rollback) for the package's explicitly declared compatible baseline.

## Export current suppression evidence

This first-packaged-key-enrollment procedure applies only to single-workspace mode with a database already on the compatible initial migration baseline. Shared-workspace mode denies these suppression HTTP routes; its fresh provisioning enrolls the key before activation. It cannot bypass historical-schema rejection and does not qualify recovery after later erasure.
For a compatible Source-only database lacking a protected key check, obtain a current administrator token through the configured Authorization Code + PKCE client.
Keep it in a private curl configuration, never shell arguments/history/logs:

```sh
umask 077
mkdir /secure/evidence-new
install -m 0600 /dev/null /secure/export-auth.conf
```

With a trusted editor add `header = "Authorization: Bearer <token>"`, and `cacert` for a private CA; never enable `insecure`.
Export from the trusted installation:

```sh
curl --config /secure/export-auth.conf --proto '=https' --fail --silent --show-error \
  --request POST --output /secure/evidence-new/manifest.json \
  https://photographs.example.invalid/management/retention/erasures/suppressions/exports
```

Read only `snapshotId` and `pageCount`, without printing protected payloads.
Download every index from zero through `pageCount - 1` for the same snapshot:

```sh
curl --config /secure/export-auth.conf --proto '=https' --fail --silent --show-error \
  --output /secure/evidence-new/page-000000.json \
  'https://photographs.example.invalid/management/retention/erasures/suppressions/exports/<snapshot-guid>/pages/0'
```

Use six-digit filenames for subsequent pages. Check matching snapshot IDs/page indexes; any failed or missing page makes the export incomplete.
Only Server authenticates the protected payloads with the retained deployment key.
Preserve the complete export independently with its origin/time, remove the token configuration, and follow first-enrollment instructions in the package guide.
