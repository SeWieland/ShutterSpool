# Back up and recover a stopped installation

Use a maintenance window and standard Docker, PostgreSQL and archive tools.
Commands run from the extracted package directory on Linux/WSL2 with the installation's complete `COMPOSE_FILE` selection.
Use a privileged operator shell where file ownership requires it, and `umask 077` for backup work.
Never use `down -v`, volume pruning or a fresh database with an old spool to repair startup.

Examples below cover bundled PostgreSQL and Keycloak.
External-service owners provide equivalent database/role and identity custody during the same application shutdown interval.
Do not stop a shared external IdP or dump unrelated databases: preserve that issuer under its provider's recovery procedure.
Valkey and previews are disposable; destination storage has separate custody.

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
Drain, capture a verified recovery set, replace package/image/configuration and allow migrations/recovery before reopening traffic.
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

This first-packaged-key-enrollment procedure applies only to a database already on the compatible initial migration baseline. It cannot bypass historical-schema rejection and does not qualify recovery after later erasure.
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
