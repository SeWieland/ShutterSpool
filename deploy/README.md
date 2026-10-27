# Install a packaged version

Choose an experimental release before the project has a stable `1.x` release. Every `0.x` version and every prerelease suffix is experimental. Unsuffixed releases beginning with `1.0.0` carry the documented stable compatibility promise.

This package targets one Server instance on one Linux amd64 Docker host.
Inspect `release-manifest.json` for the version, source commit, image digest, dependency versions and compatibility limits.
A build alone does not qualify compatibility; follow the instructions and limits published with that release.
The observed test environment is Docker Desktop/WSL2 with Linux volumes. Native Linux storage, ARM and public FTPS require separate qualification.

## Get a published release

Download `shutterspool-compose.tar.gz` and `shutterspool-compose.tar.gz.sha256` from the chosen [GitHub Release](https://github.com/SeWieland/ShutterSpool/releases). Verify the checksum, then extract the package in a new private Linux working directory:

```sh
umask 077
mkdir install
sha256sum --check shutterspool-compose.tar.gz.sha256
tar -xzf shutterspool-compose.tar.gz -C install
cd install
```

Continue with [Configure and start](#configure-and-start) from the extracted `install` directory.
The generated `.env` pins `SERVER_IMAGE` to the official image digest, which Docker Compose pulls anonymously from GHCR.
This archive contains configuration and operator instructions; its image is stored separately in GHCR, so there is no local image archive to load.
The manifest inventories the distributed package files and records the source commit and image digest. The Compose file pins that public image by digest.

## TL;DR

For a package with bundled PostgreSQL, Valkey, Keycloak and Traefik:

1. [Download, verify and extract a published package](#get-a-published-release), then open a shell in the extracted directory.
   For a local build instead, use its output directory and load its retained image with `docker load --input server-image.oci.tar`.
   Prepare private file directories and configuration:

   ```sh
   umask 077
   mkdir -p secrets certificates restore-evidence
   cp config/server.json.example config/server.json
   cp config/identity.env.example config/identity.env
   ```

2. Edit the generated `.env`, preserving its version and `SERVER_IMAGE`; keep the default `COMPOSE_FILE` selection.
   Choose a unique project name, free network subnets and reachable HTTPS/ingest addresses.
   Set matching origins and administrator UUID in `config/server.json` and `config/identity.env`, and replace both Host rules in `package/traefik/dynamic.yml`.
   The [configuration steps](#configure-and-start) identify the required values.
3. Supply the [secret files for the bundled services](#configure-and-start) and [HTTPS/FTPS certificate files](certificates/README.md#required-files).
   Apply the documented consumer ownership and permissions before starting; certificates come from the operator or an external issuing service.
4. Validate and start the installation, then check Server readiness:

   ```sh
   docker compose config --quiet
   docker compose up --detach --wait --wait-timeout 240
   docker compose exec -T server dotnet ShutterSpool.Server.dll --health-check
   ```

Open the configured application HTTPS origin and sign in with the initial administrator configured in `config/identity.env`.
Use the [component choices](#choose-components) for external services or another proxy, and the [maintenance guide](operations/README.md) before backup or upgrades.

## Prerequisites and custody

Operators need Docker Engine/Compose, Linux storage, standard PostgreSQL/archive tools for maintenance, and OpenSSL for supplied-certificate checks.
Extracting a published Compose archive needs `tar` with gzip support and `sha256sum`.
They do not need source, Aspire, Node or the .NET SDK.
Keep package/configuration on a private Linux filesystem, with a unique Compose project name.
Do not use `docker compose down -v`, volume pruning or a new database with an old spool as troubleshooting.
Ordinary `docker compose down` preserves named volumes.

Reserve space for spool, in-flight/temp/quarantine bytes, disposable previews, PostgreSQL, logs and complete backups.
Backup/restore requires additional copies; qualification measurements are not a production sizing promise.
Use operator-managed encryption for storage and backups, and separately protected recovery keys.
The deployment key encrypts credentials/evidence, not photographs.

## Choose components

The common `compose.yaml` contains Server, Imaginary and enabled camera edges.
Select additive files with `COMPOSE_FILE` in `.env`; Linux uses `:` as the separator.
The supplied default selects PostgreSQL, Valkey, Keycloak and Traefik:

```text
COMPOSE_FILE=compose.yaml:components/postgres.yaml:components/valkey.yaml:components/keycloak.yaml:proxies/traefik.yaml
```

| Choice | Files/configuration |
| --- | --- |
| External PostgreSQL | Omit `components/postgres.yaml`; provide application database secret and bundled Keycloak's separate database settings if it remains enabled. |
| External Valkey | Replace `components/valkey.yaml` with `components/valkey-external.yaml`; supply `secrets/valkey-connection`. |
| External OIDC | Omit `components/keycloak.yaml`; configure the existing issuer/client and explicit application grant. |
| nginx | Replace `proxies/traefik.yaml` with `proxies/nginx.yaml`. |
| Existing Docker proxy | Select `proxies/existing.yaml`; set `PROXY_NETWORK` to its dedicated external network. No bundled proxy or public Server port is added. |

Choose exactly one proxy option and one Valkey option. Dependency choices are independent.
For example, external PostgreSQL/OIDC with bundled Valkey uses core + Valkey + existing proxy.
Append operator overrides to `COMPOSE_FILE`; use the same complete selection for every lifecycle/maintenance command.
Do not start overlapping Server instances with different file selections against the same database/spool.

## Configure and start

1. Open the extracted package in its private Linux directory and retain its manifest.
   For a local build, use its output directory and load its retained image with `docker load --input server-image.oci.tar` if not already loaded.
   A published release uses the exact public image digest supplied in its package; no source checkout, SDK or registry credentials are needed.
   Operators should not rebuild a qualified image.
2. Edit the generated `.env`, preserving its version and digest-pinned `SERVER_IMAGE` reference; `.env.example` is an unpopulated reference template.
   Populate the configuration examples under `config` as directed by their filenames.
   Set application/issuer HTTPS origins, persistent administrator UUID, ingest hostname, listen/advertised IPv4 addresses and ports.
   For default Traefik, replace both example Host rules in `package/traefik/dynamic.yml` with the application and public issuer hostnames.
   Select a free `BACKEND_SUBNET` for the private application network.
   Select a distinct free `IDENTITY_PROXY_SUBNET` for bundled Keycloak's proxy network; Keycloak trusts forwarded headers only from that network.
   Set `PROXY_SUBNET` to a free dedicated proxy-network CIDR; Compose supplies it to `ShutterSpool.ForwardedHeaders.KnownNetworks`.
   Do not trust the entire backend or cluster network. An existing proxy must overwrite forwarded headers and be the only trusted forwarding source.
3. Keep `server` and `localhost` in `AllowedHosts` alongside the public application hostname for private callbacks/probes.
   Ensure public application/issuer DNS works for browsers and containers, or explicitly configure the private OIDC backchannel below.
4. Create private `secrets`, `certificates` and `restore-evidence` directories. Supply the selected secrets without printing them to terminals, command history or logs.
   Use a persistent random base64 32-byte encryption key and a random callback key of at least 64 characters.
5. Supply the HTTPS and enabled-FTPS certificates described in the [certificate guide](certificates/README.md).
   Consumers mount them read-only; no ACME/DNS credentials belong in this installation.
6. Check consuming-user permissions, render the complete graph, then start:

   ```sh
   docker compose config --quiet
   docker compose up --detach --wait --wait-timeout 240
   docker compose ps
   docker compose exec -T server dotnet ShutterSpool.Server.dll --health-check
   ```

| Secret file | Consumer |
| --- | --- |
| `database-connection` | Server; complete Npgsql connection string, bundled example `Host=postgres;Database=shutterspool;Username=postgres;Password=<password>` |
| `encryption-key` | Server; preserve with the matching database and backup |
| `camera-callback-key` | Server and enabled edges |
| `postgres-password` | Bundled PostgreSQL and bundled Keycloak's configured database connection |
| `keycloak-admin-password` | Bundled identity bootstrap/reconciliation only |
| `administrator-password` | Initial application identity creation only |
| `valkey-connection` | Server when external Valkey is selected |

Server runs as UID/GID 1654; Keycloak/SFTPGo use UID/GID 1000.
Use restricted host permissions for actual consumers: single-consumer secrets can use mode 0400 with the matching owner; the shared callback key uses owner 1000, group 1654 and mode 0440.
Compose file-backed secrets are not an encrypted vault, and `uid`/`mode` declarations do not repair host ownership.
Check the selected proxy's certificate access as well.
File secrets are read at startup: replace them under operator control and restart affected services.
Never regenerate the deployment encryption key or bootstrap subject on ordinary restart.

Server's chiseled runtime contains no shell/curl/package manager. It retains public CA trust, ICU and timezone data.
`/health/live` reports process liveness; `/health` and `/health/ready` include PostgreSQL readiness.
Migrations and recovery complete before HTTP opens. Valkey, previews and telemetry do not gate authoritative recovery.
Logs are bounded/redacted; OTLP is off unless configured. Never enable sensitive payload/SQL logging or attach media/tokens to reports.

A Local destination requires its own persistent mount, writable by UID/GID 1654 and separate from the spool.
Include it in an operator override, for example at `/deliveries`, and retain its storage across restores for conflict reconciliation.
Do not deliver into the container's disposable writable layer.

In **Delivery**, select the Local destination and open **Settings**. Enter the absolute path visible inside the Server container, such as `/deliveries`, or use **Browse…** and **Use this folder** to choose it.
Browse shows the server's filesystem, not folders on the computer running your browser, and does not create missing folders or mounts.
Choose **Save and test folder** to save the root and check write permission and supported publication; selecting a folder alone does not save or test it.
**Test saved settings** becomes available once a folder is saved and there are no unsaved folder edits.

## Identity and proxy integration

The browser and API share the application HTTPS origin.
Bundled Keycloak uses a fixed public `KC_HOSTNAME` and a separate `KC_HOSTNAME_ADMIN`; the package does not publish an admin route.
Administrative endpoints and the master realm must stay private even on the public hostname: a separate admin URL alone is not access control.
If administration access is needed, provide an explicitly restricted route separately.

Initial reconciliation creates the application identity and its explicit issuer/subject grant; there is no first-login enrollment.
Repeated reconciliation preserves existing passwords, subjects and grants. The package owns its application client redirect/audience/role-mapper settings.
If initial account creation is interrupted before its password is set, reconciliation keeps Server closed on retry.
A trusted Keycloak operator must verify the configured subject and complete that account's password through private administration, then rerun `docker compose run --rm --no-deps keycloak-reconciler` before starting the graph.
Reconciliation never restores a password an operator removed; an existing passwordless bootstrap account needs explicit operator resolution.
External OIDC requires no IdP administrator credential in ShutterSpool.
Remove bundled identity routes from the proxy examples when using an external issuer; do not route to a nonexistent `keycloak` service.

The supported OIDC contract requires discovery/JWKS, public-client authorization code + PKCE S256, JWT access tokens for the configured audience, browser CORS, exact callback/logout URLs and stable issuer/subject identity.
Use `/auth/callback` and `/auth/signout` on the application origin.
The administrator token role and local grant are both required.
The default role is `shutterspool-admin`; set `ShutterSpool.Security.AdministratorRole` for another role name.
Set `BootstrapIssuer` to the exact public issuer and `BootstrapSubject` to the administrator's existing stable subject to establish the initial local grant.
Changing an issuer or subject requires deliberate grant migration; matching email addresses do not transfer access.
`AdministratorRoleClaimType` can select a provider's flat role claim; its default preserves the existing `role` claim mapping.
Nested provider-specific claims need a compatible provider mapping; arbitrary IdPs are not qualified merely because discovery succeeds.
Do not infer another provider's compatibility from the Keycloak configuration; verify its claims, PKCE, refresh and sign-out behavior explicitly.
Sign-out clears the local browser session and attempts the provider's end-session flow. A provider session may survive when that flow is unavailable or fails; logout does not revoke already issued access tokens by itself.
When bundled Keycloak uses external PostgreSQL, configure `KC_DB_URL_HOST`, `KC_DB_URL_DATABASE` and `KC_DB_USERNAME` in `config/identity.env`, and supply its database password secret.

Public HTTPS discovery is default. For a private Keycloak backchannel, keep `Authority` and `BootstrapIssuer` on the exact public realm issuer, set `KC_HOSTNAME_BACKCHANNEL_DYNAMIC=true`, and configure:

```json
"MetadataAddress": "http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration",
"AllowInsecureMetadataAddress": true
```

These fields belong inside `ShutterSpool.Security` in `config/server.json`.
The HTTP opt-in is only for an explicitly isolated backend network; prefer HTTPS for external infrastructure.
Token validation still requires the public issuer. Browser authentication continues to use public HTTPS.
For private CAs, provide a complete trusted bundle to each relevant runtime; never disable TLS validation.

Both proxy examples require correct forwarding headers, WebSocket support, private callbacks and externally supplied TLS.
Traefik uses its file provider without Docker socket access. Existing proxies use Server port 8080; callback port 8081 stays private.
When using bundled Keycloak with an existing proxy, also attach that proxy to the project's `<COMPOSE_PROJECT_NAME>-identity-proxy` network to reach Keycloak port 8080.
Keycloak is deliberately absent from Server's trusted proxy network.
Existing proxies attached to the identity network must overwrite forwarded host, port, protocol and client-address headers; do not attach unrelated clients to either proxy network.
These restrictions follow [Keycloak's proxy trust guidance](https://www.keycloak.org/server/reverseproxy#_trusted_proxies).

## Camera profiles

The default core enables modern explicit FTPS. A separately generated all-edge core enables the qualified trusted-LAN legacy/plain options; select it only when deliberately exposing those profiles.
Replace the first `compose.yaml` entry in `COMPOSE_FILE` with `profiles/all-camera-edges.yaml`; retain the selected component and proxy entries.
For this nested core, use `docker compose --project-directory .` for every command from the extracted package root so configuration and secret mounts resolve there.
An enabled edge may exist before any Source; Source changes never create services.
Modern defaults: control 2121, passive 2122–2137; legacy: 2138 and 2139–2154; plaintext: 2155 and 2156–2171.
Match published control/passive ports to the graph and advertised camera-reachable IPv4 address.
HTTP reverse proxies do not provide FTPS routing. Only explicitly selected paths belong in firewall/NAT rules.
Invalid certificates never enable plaintext fallback.
Drain before profile withdrawal using the [maintenance procedure](operations/README.md#stop-writers-before-capture); retain Source settings and recovery custody on timeout.

## Backup, restore and upgrades

Follow the [standard maintenance-window procedure](operations/README.md).
Stop application/edge/identity writers, leave PostgreSQL available for logical dumps, copy the entire spool, and preserve matching keys/configuration/identity.
External-service owners retain their corresponding backup, availability and upgrade responsibilities.
Supported versions/settings are the release's stated matrix, not all PostgreSQL/Redis-compatible/OIDC vendors.

Ordinary restore uses fresh isolated storage with no intervening privacy erasure.
Recovery predating erasure or with uncertain erasure history is unsupported; existing guards remain enforced.
Upgrade only from the named compatible baseline. Rollback restores complete matching state, not a database schema downgrade.

### Key validation on a compatible database

The package declares `initial-create` with initial migration `20261025211318_InitialCreate` and `fresh-install-only`: no historical upgrade predecessor is supported. Fresh databases enroll a protected key check; restarts validate the retained key.
The following recovery guard applies only to a database already on this baseline; it cannot bypass schema rejection. Existing checks or decryptable destination credentials validate the retained key.
A Source-only database without a key check cannot prove that key from password verifiers.
Before stopping the compatible trusted instance, [export its complete current suppression evidence](operations/README.md#export-current-suppression-evidence).
Copy the manifest/pages to `restore-evidence` with directory mode 0500/files 0400 owned by UID/GID 1654.
Add inside `ShutterSpool` in `config/server.json`:

```json
"Restore": {
  "Enabled": true,
  "SuppressionSnapshotId": "<exported-snapshot-guid>",
  "EvidenceDirectory": "/run/restore-evidence"
}
```

Keep proxy/edges stopped and recreate Server privately. Check readiness with its managed health command, retained identities, original hashes and curation.
Wrong-key/incomplete evidence leaves startup closed. After successful enrollment disable the one-time Restore setting and recreate/recheck Server.
This key enrollment procedure does not qualify disaster recovery after later erasure.
Every historical migration chain, including the former Source-profile and Collection schemas, has no supported conversion to this initial baseline.
Follow [moving from an older pre-alpha installation](operations/README.md#moving-from-an-older-pre-alpha-installation) for the fresh database/spool transition and retained-state requirements.
