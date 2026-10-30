# ShutterSpool.Server

Server is the entry point for authorized HTTP, private media, live updates and hosted workflow loops.
It translates requests into Application calls; PostgreSQL and Domain policies decide durable outcomes.
The Nuxt production build supplies the static SPA served from `wwwroot`.

Read the [architecture](../../docs/architecture/system-boundaries.md) and [core workflow](../../docs/core-workflow.md) before the configuration/API reference below.
`Api` owns endpoint-local DTOs, `Hosting` schedules work, `Security` enforces access, and only `Composition` imports Infrastructure.

## Build and generated contracts

From a clean checkout, run `dotnet tool restore`, `dotnet restore --locked-mode`, `dotnet build --no-restore`, and `dotnet test --no-build` from the repository root.
Run `npm run build` in `../ShutterSpool.Web` to generate the SPA into `wwwroot`.
The build emits `openapi/ShutterSpool_Server.json` for the generated TypeScript client; the build-time tool accepts only alphanumeric, underscore, space, and dash filenames.

## Local security configuration

Management endpoints require a verified OIDC bearer token and an active PostgreSQL-backed local grant.
Single-workspace mode also requires the `shutterspool-admin` token role; shared-workspace mode checks the selected tenant's grant without requiring that provider role.
Set `ShutterSpool__Security__Authority`, `ShutterSpool__Security__Audience`, `ShutterSpool__Security__BootstrapIssuer`, and `ShutterSpool__Security__BootstrapSubject` through deployment configuration.
Set `ShutterSpool__Security__EncryptionKey` to a persistent base64-encoded 32-byte deployment secret outside PostgreSQL; it must accompany backups and is never committed, logged, or stored in Valkey. Packaged deployments can provide it through `ShutterSpool__Security__EncryptionKeyFile`; the file contents override the inline value.
Packaged deployments set `ShutterSpool__Deployment__Packaged=true` and supply explicit HTTPS OIDC authority/audience, the encryption key and trusted proxy configuration.
Single-workspace mode additionally requires `ConnectionStrings__shutterspool` (or `ConnectionStrings__shutterspoolFile` containing the full connection string), bootstrap issuer/subject and spool root.
Shared-workspace mode uses the control and tenant connections and storage identities described below instead of the single-workspace database/root/bootstrap settings.
Optional external Valkey uses `ConnectionStrings__valkeyFile` containing its connection string.
Startup records a protected key-check marker and rejects a changed key before recovery or traffic; existing destination credentials are checked before marker enrollment.

A first packaged startup against a compatible initial-baseline database without a key check, with Source credentials but no decryptable destination credentials, must authenticate the current suppression snapshot through the restore gate before recording the marker. This gate cannot make a historical migration chain compatible.
The configuration keys are `ShutterSpool__Restore__Enabled`, `ShutterSpool__Restore__SuppressionSnapshotId` and `ShutterSpool__Restore__EvidenceDirectory`.
The [compatible-database key validation procedure](../../deploy/README.md#key-validation-on-a-compatible-database) shows their `config/server.json` representation, evidence ownership, readiness verification and one-time gate removal.
Missing or invalid proof leaves the key marker absent and startup closed.
Fresh empty databases enroll normally; subsequent restarts validate the marker before any restore or recovery work.

When camera ingress is enabled, `ShutterSpool__CameraIngress__CallbackKeyFile` can supply its callback key.
File paths must be readable by the Server container user and files should be restricted by the host; they are not encrypted stores.
For HTTPS proxy deployments, configure `ShutterSpool__ForwardedHeaders__KnownNetworks__0` with the proxy's private CIDR. Only those networks may supply forwarded host, scheme and client address headers.
The host shutdown deadline is configurable with `ShutterSpool__ShutdownTimeoutSeconds` (default 120 seconds); a clean process exit within the operator's deadline remains the shutdown evidence.
In single-workspace mode, changing the bootstrap issuer or subject and restarting the Server revokes the preceding bootstrap grant.
Shared-workspace mode provisions its initial grant once and never replays it on ordinary restart.

Management HTTP DTOs are finite and endpoint-local. The management group caps request bodies at 2 MiB and rejects unknown JSON properties; list endpoints use validated keyset cursors (30 by default, 100 maximum). Configuration writes carry an expected revision and return the authorized current redacted representation on conflict. Capture curation accepts rating -1 through 5, optional fixed-vocabulary label and tags; selection is response-only and derived. Batch actions require 1–100 distinct Capture IDs and recheck the active management grant before each item.

## Shared workspace provisioning

Shared-workspace mode serves up to 32 workspaces from one Server, with a separate PostgreSQL database, restricted runtime role and fixed filesystem allocation for each.
The [packaged operator procedure](../../deploy/operations/README.md#shared-workspace-provisioning) owns configuration, provisioning, upgrades and allocation changes, including stopped-writer and recovery requirements.
Existing single-workspace data is not adopted or reset; normal shared-workspace startup validates compatible state without migrations or initial-grant replay.

### Request and worker scope

`GET /workspaces` requires authenticated OIDC and returns only the caller's current grants as `items: [{ id, name }]`, plus `incomplete` and `selectionRequired`.
An incomplete result means discovery could not establish every eligible workspace; it is not proof that an omitted membership does not exist.
Shared-workspace requests require `X-ShutterSpool-Workspace` with the selected workspace GUID. Only the live transport endpoints accept the equivalent `workspace` query parameter.
Live negotiation tokens are bound to that workspace; reusing one with a different selector is denied even when the caller belongs to both workspaces.
Missing, conflicting or unavailable scope fails closed. The Server never substitutes another workspace or stores a mutable workspace selection in the login session.
The Web client keeps selection in its page URL and reloads on switching; fetched private preview blobs use the same authenticated scope.

Each worker pass has a fresh immutable tenant context. Workers rotate across eligible tenants with bounded work per turn and skip unavailable/idle tenants.
Delivery and preview have two shared slots per work type; the other workflow loops have one. A tenant has at most one active pass per work type.
Tenant connection pools have a maximum of four and no minimum idle connections. These bounds do not promise a per-tenant throughput or availability level.

## Camera credentials

Set `ShutterSpool:CameraCredentials:MinimumPasswordLength` (environment `ShutterSpool__CameraCredentials__MinimumPasswordLength`)
through administrator-controlled deployment configuration. The default is 12; allowed minimums are 1–128 and the fixed maximum is 128.
Passwords accept exact non-space printable ASCII (`!` through `~`), without trimming or normalization.
A minimum below 12 deliberately permits weaker protection. Invalid configuration fails startup; changing it governs subsequent
creation/replacement and does not revoke existing credentials. AppHost forwards the same key.

Authorized `GET /management/source-capabilities` includes the effective `passwordPolicy` requirements.
Source creation and revision-guarded `POST /management/sources/{id}/regenerate-secret` accept optional `username`,
`passwordMode` (`Generated` or `Custom`) and `password`. Omission preserves generated provisioning;
custom mode requires a supplied valid password. A supplied username uses 1–64 ASCII letters/digits/underscore/hyphen,
beginning with a letter or digit, and is unique with exact case across retained Sources, including archived Sources.
Replacement retains the username when omitted and atomically advances configuration revision and credential generation.
It affects every sharing device and blocks new work from stale sessions; previously admitted transfers retain completion guarantees.
The Source identity, root, connection profile, media and history remain intact.

Generated usernames combine two short photography-themed words, such as `softglow` or `tinyzoom`: 6–8 lowercase letters,
with no prefix, separator or digits. The database enforces uniqueness and creation retries a collision up to five total attempts.
Existing usernames stay unchanged. Generated passwords use at least 20 characters
from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789` (or the configured minimum, if greater).
Only generated passwords appear once in successful responses; custom passwords remain write-only and ordinary reads expose neither.
After an uncertain mutation, reread configuration before deliberately replacing a lost generated password; reads cannot recover it.
Successful FTP authentication initializes/checks the guarded virtual root before exposing the login; no manual root creation is needed.

## Private Capture previews

Previews support browsing; the [workflow guide](../../docs/core-workflow.md#3-let-the-photograph-lead-curation) explains why they never gate acceptance or release. The values below are the runtime reference.

`GET /media/previews/{captureId}/grid` and `/detail` require the same verified OIDC token and active local grant as other private media, including the selected workspace in shared mode. Single-workspace mode additionally requires the `shutterspool-admin` token role. The one-segment route uses `grid`. Ready WebP responses use `Cache-Control: private, no-cache` and an ETag: the browser may store the bytes but must revalidate, and an authorized unchanged request receives `304`. Pending, unavailable and missing responses use `private, no-store`; `X-Preview-State` describes the state. RAW-only Captures return a static SVG with `pending`; failed or inaccessible JPEG generation returns the SVG with `unavailable`.

The Server resolves the current primary JPEG from PostgreSQL and sends only its committed bytes to a private Imaginary `/fit` request on a cache miss. Set `ShutterSpool:Preview:ImaginaryUrl` to the private service URL. In single-workspace mode, set `ShutterSpool:Preview:CacheRootPath` to a private writable directory separate from originals. Shared-workspace mode instead fixes each cache at `<workspace-storage-root>/previews` inside its assigned allocation; a deployment-level cache path does not override that boundary. Cache files are disposable; deleting them regenerates derivatives while the JPEG original remains. Keep the cache root writable only by the Server. Imaginary needs neither a spool mount nor remote URL fetching.

`ShutterSpool:Preview:PrewarmMode` defaults to `GridOnly`. `Off` leaves generation to authorized requests; `GridOnly` prepares the grid after a primary JPEG is grouped; `Full` prepares grid and detail. PostgreSQL holds pending work and retry leases so Server restarts and Imaginary outages do not lose prewarming. Invalid JPEG input stops background retries; temporary failures back off. An authorized request still generates a missing derivative on demand.

Set `ShutterSpool:Preview:GridWidth`, `GridHeight`, `DetailWidth`, and `DetailHeight` to change the WebP maximum dimensions. Defaults are 480×480 and 2048×2048; each dimension must be 64–4096 pixels. JPEG input is capped at 64 MiB and 60 megapixels, and output at 4 MiB. `MaxConcurrentGenerations` defaults to 32 per Server replica (valid range 1–128); `GenerationQueueTimeoutSeconds` and `GenerationTimeoutSeconds` default to 60 and 90 (valid range 1–600). The AppHost exposes corresponding parameters and separate Imaginary throttle, CPU and HTTP-timeout parameters. Preview faults never change Capture or Asset state.

## Capture inbox and live updates

Gallery slideshow reads can pass `startAt=<Capture ID>` on the first inbox page to begin inclusively at a matching Active Capture.
If that Capture does not match the authorized query, paging starts at the first match instead.
Use the returned cursor for subsequent pages; `startAt` and `cursor` together are rejected.
This keeps slideshow startup bounded without a persisted playlist or an up-front enumeration of the Gallery.

The authorized `/management/sources/{id}/display` route uses a local HybridCache for a bounded, non-secret Source projection (30-second absolute expiry, at most 256 identities and 4 KiB payloads). Successful Source mutations invalidate after commit. Configuration editor reads and workflow decisions remain PostgreSQL-authoritative.


Capture reads support inclusive signed-rating intervals (`rating`/`ratingMaximum`) and a label union (`labels`, including `None`)
to the existing filter-bound Capture cursor. `/management/tags?q=...` returns at most twelve case-insensitive
substring suggestions for nonempty queries up to 64 characters. `/management/destinations/{id}/work` pages
delivery history by immutable creation time/ID with an optional exact `state` filter; rows include Capture identity.
Preview routes accept `maximumDimension` from 64 to 4096, with the requested size included in derivative cache identity.
These reads use existing management/media authorization and Application ports. Released read-only behavior is
a web-workspace restriction; the existing server's versioned current-curation policy remains intact.

`GET /management/captures?limit=30&cursor=...` returns at most 100 grouped Captures, newest first by default. Optional `oldestFirst=true` reverses immutable first-acceptance timestamp/ID ordering before paging; cursors bind both direction and filters. Ungrouped Assets are never shown as Captures. `GET /management/captures/{id}` combines current/frozen curation, variant identities, the current JPEG preview Asset ID and compact delivery summaries. Decisions, deliveries and explicit actions have separate paged Capture routes; attempts have a paged Delivery route. Decision and delivery rows include nullable destination display names, including archived configurations. All use the same OIDC administrator role and active local grant as mutations. Inbox responses contain no original paths, hashes or bytes; authorized preview endpoints remain the only browsing media reads.

`GET /auth/config` exposes only the public OIDC `authority`, `clientId` and `audience` settings. Set `ShutterSpool:Security:ClientId` for a non-default public client. Configure exact browser origins in `ShutterSpool:Security:AllowedOrigins` when the SPA is cross-origin; same-origin needs no entry. HTTP origins are accepted only for loopback development. `/live/changes` is the authorized SignalR hub; bearer query authentication is accepted on its transport and negotiate paths only. It closes when the token expires and rechecks the active local grant every 30 seconds. Browser origins must match the request origin or the configured exact list. Hub hints carry Capture/Asset IDs and change kinds only; clients reread authorized HTTP state. Missing or dropped hints do not alter PostgreSQL workflow state.

`GET /management/sources/authentication-observations?sourceIds=<guid>` reads outcomes for 1–100 distinct Sources. It returns only Source identity, generic outcome, time and a persisted monotonic version; a missing item means no saved outcome, not proof that authentication never occurred. Camera auth queues observations outside its response path. A bounded in-process worker coalesces per Source, commits PostgreSQL state, then emits `sourceAuthenticationChanged`; overflow or write failure marks current coverage incomplete. A verified secret rejected because ingress is disabled or the trusted legacy-FTPS route is not enabled for that Source may be attributed. Unknown or wrong-secret failures never select a Source. The durable row is deleted with its Source.

The Server excludes hub requests from OpenTelemetry HTTP spans because SignalR's standard WebSocket/fallback transports may carry the bearer in `access_token` query parameters. ASP.NET hosting and SignalR request URL logs remain at Warning or higher; do not enable full connection URL logging in the Server or reverse proxy.

Management Source/Destination DELETE accepts `expectedRevision` and returns `outcome: deleted | archived`.
Only configurations without related media/work/history are removed; referenced rows archive and retain readable
history while leaving active lists. Archive uses existing ingress/routing fences and never deletes original/provider
files. Capture reads accept repeated `sourceIds` (max 30) and `workflows` (Waiting/Held/Released/Attention, max four),
OR within a dimension and AND across dimensions before paging, with normalized filter-bound cursors.
Inbox Assets expose available camera make/model; detail exposes byte size for routing-fact inspection.

## Collections API

Use the [Collection guide](../../docs/collections.md) for the photographer's workflow and naming/routing consequences.
These endpoints share the existing management administrator/local-grant authorization; Source credentials grant no access.
The emitted OpenAPI document owns exhaustive request/response schemas.

| Operation | HTTP contract |
| --- | --- |
| Search/read | `GET /management/collections?q=...&limit=30&cursor=...`; `GET /management/collections/{id}` |
| Create | `POST /management/collections` with `{ name }` |
| Rename | `PUT /management/collections/{id}` with `{ expectedRevision, name }` |
| Delete unused Collection | `DELETE /management/collections/{id}?expectedRevision=...` |
| Move one Active Capture | `PUT /management/captures/{id}/collection` with `{ expectedRevision, collectionId }` |
| Move explicit selection | `POST /management/captures/collection-moves` with `{ targetCollectionId, items: [{ captureId, expectedRevision }] }` |

Collection search accepts at most 128 characters, defaults to 30 results and permits 1–100 per page; its cursor binds the normalized search.
Responses distinguish short `name` from full relative `path` and include a stable ID, revision, built-in flag and bounded advisory Source/Capture reference counts (1,001 denotes more than 1,000).
The built-in Collection is immutable. Rename/delete conflicts return authorized current state; deletion checks all references, including archived Sources and retained Capture records.
Source create/update accepts `collectionId`; creation defaults to `ShutterSpool`, and updates use the existing configuration revision.

Capture reads accept repeated exact `collectionIds` (maximum 30), OR within the dimension and AND with other filters before paging; cursors bind those IDs.
Inbox/detail expose `currentCollection`; detail separately exposes nullable `releasedCollection`, and routing history carries recorded Collection facts.
Null historical facts mean Collection was not recorded, rather than a historical default assignment.
Routing conditions accept `collectionName`/`collectionPath`; the multi-segment `{collectionPath}` token is folder-only.

Bulk moves require 1–100 distinct explicit IDs and nonnegative Capture revisions.
Results report `applied`, `conflict`, `notFound`, `notMovable` or `targetMissing` per Capture, with current state when available.
Malformed envelopes cause no writes; valid requests can partially commit, including before an infrastructure interruption.
Reread the requested Captures after an uncertain response and retry only unresolved items against reviewed revisions.
Collection operations preserve release/delivery history; moves remain independent from released-curation editing.

## Retention API

Read the [retention guide](../../docs/retention.md) for policy, lifetimes and the consequences of each action. This section maps those actions to HTTP.

Capture inbox and detail responses include the authoritative `retention` summary when available; inbox filtering accepts `favoritesOnly=true`. Owners can set a Capture Favorite with revision-checked `PUT /management/captures/{id}/favorite`, move an Active Capture to recoverable Trash with `POST /management/captures/{id}/trash`, restore through `POST /management/trash/{id}/restore`, and resume existing paused work through `POST /management/captures/{id}/resume`. The existing curation `POST /management/captures/{id}/restore` remains a separate policy action.

After all current members have been routed, an owner may record explicit unmatched intent with `POST /management/captures/{id}/no-delivery-intent` and `{ expectedRevision }`. The action is rejected if any matched decision or Delivery history exists, and a later matched intent clears the disposition in the same routing transaction.

`GET /management/trash?pageSize=30&cursor=...` uses stable keyset paging. `GET /management/retention/settings`, `POST /management/retention/settings/preview` and `PUT /management/retention/settings` expose revisioned policy, quota and preview-cache settings. `GET /management/retention/capacity` and `/management/retention/history?pageSize=30&cursor=...` report current bounded capacity and committed history. These routes use explicit Server DTOs generated from OpenAPI; Application records are not transport contracts.

The settings preview reports `accountingKnown`, `usedBytes` and `reservedBytes` from one capacity snapshot.
`proposedQuotaHeadroomBytes` subtracts usage and reservations from the proposed active quota; it can be negative, and is null when accounting is unknown or the proposed quota is inactive.
`overQuota` and `admissionBlocked` still describe the current saved configuration, independently of that proposed-limit comparison.

The retention summary carries policy revision, tier, deadlines, protection and pause reasons, variant byte lengths and states, and a server-observed interval timeline. A start is included only when it is derived from durable accepted/confirmation evidence. Trash restore does not reinstate paused release/delivery work; Resume remains explicit.

## Delivery work

PostgreSQL holds due deliveries, attempts and renewable claims. The worker scans it even when a Valkey wakeup is missed or Valkey is unavailable. `ShutterSpool:DeliveryWorker:LeaseDurationSeconds`, `LeaseRenewalSeconds` and `ScanIntervalSeconds` default to 60, 15 and 2. The renewal interval must be shorter than the lease. Delivery history is grouped in the authorized Capture routing endpoint.
The [delivery boundary guide](../ShutterSpool.Infrastructure/README.md#destination-delivery-boundaries) maps the worker, adapter, filesystem operation and configuration service to their owners.

`ShutterSpool:Destinations:EnabledKinds` is the operator's list of available kinds (`local`, `webdav`).
Single-workspace mode defaults to both; shared-workspace mode defaults to none unless explicitly configured.
`GET /management/destinations/capabilities` returns `enabledKinds` for the current workspace UI.
Disabling a kind retains saved configuration and history, but blocks creation, configuration, probes, delivery and reconciliation; it does not delete media or confirm uncertain work.
Apply deployment capability changes during stopped-writer maintenance.

Local and WebDAV adapters implement the same Application delivery port. Create a destination with `POST /management/destinations`, supplying `name` and `kind: "local"` (no secret is needed for local storage), then set `rootPath` with `PUT /management/destinations/{id}/local`. `GET` on that path reads the configured root; `POST /management/destinations/{id}/local/test` checks reachability, write permission and no-replace publication without changing delivery work. These endpoints require the management authorization above. A root is absolute and visible to the Server, must already exist and remain writable, and cannot change after that destination has delivery work.

`GET /management/destinations/local-folders?path=...` browses directories under the same management authorization.
In shared-workspace mode, browsing and delivery stay within the pre-existing `<workspace-storage-root>/destinations` directory and its descendants; omitting `path` selects that directory, and traversal or symbolic-link/reparse-point escapes are rejected.
In single-workspace mode, omitting `path` lists drives/filesystem roots.
Paths are trimmed and accept surrounding double quotes. A child-directory listing contains at most 200 entries, omits hidden/system directories and symbolic links/reparse points, and reports `truncated` when more directories exist; enter a full path to browse another folder.
The response includes the current path, parent path and directory names/paths, never file listings or contents. Browsing is read-only: selecting a folder does not create it, save destination configuration or test writability.
The web interface uses **Save and test folder** to save the selected root and then test it; **Test saved settings** requires a saved folder with no unsaved folder edits.

For WebDAV, use `kind: "webdav"`. Configure it with `PUT /management/destinations/{id}/webdav` using an absolute HTTPS collection URL, username and write-only password/app password. `GET` returns only the normalized URL and username; `POST /management/destinations/{id}/webdav/test` performs a synthetic staged upload, no-overwrite MOVE, read-back hash and occupied-target preservation probe. URL/account edits lock after delivery work exists; replacing credentials for the same identity is allowed. Provider requests use normal TLS validation and do not follow redirects. Each delivery verifies the configured collection, creates missing target parents, uploads to an attempt-specific staging object, publishes with `Overwrite: F`, and records completed publication acknowledgement. Occupied-target and uncertain outcomes use stored SHA-256/size receipts when usable, falling back to bounded streamed SHA-256 read-back. These evidence levels are distinct. A missing target during reconciliation remains ambiguous; one 404 never authorizes another write. See the [WebDAV profile and limits](../ShutterSpool.Infrastructure/README.md#tested-webdav-profile) for actual compatibility observations.

Use a host-managed root with no untrusted concurrent writers and no nested mounts below it. Configuration places a small `.shutterspool-{destinationId}.root` marker in the root; retain it with the mount. If the marker disappears, writes and reconciliation stay ambiguous until the same mounted contents return or an operator resolves the mount. The adapter stages each attempt beside its final target, verifies the original's size and SHA-256, requires a tested no-replace hard link on the target filesystem, and confirms by read-back. It never overwrites an occupied target. A matching occupied file confirms; a different or unverifiable file requires correction and an explicit Retry. Hard-link support is checked on the actual target directory, so a mount that cannot provide it is unsupported. Local read-back proves bytes visible through the mount at verification time, subject to the mount's own power-loss guarantees.

Controlled checks exercised local Windows NTFS, Windows loopback SMB (`\\localhost\C$`), Linux ext4 and a 4 MiB Linux tmpfs. The tmpfs check covered full capacity and moving the mount away and back; a Unix permission check covered an unwritable root. These observations do not establish compatibility with an external NAS. Verify the no-replace and recovery behavior on each deployment mount before using it for originals.

Private WebDAV targets require an explicit operator CIDR exception in `ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks`.
The default blocks private/local and special-use addresses. DNS is resolved and checked for every new connection; the socket connects to an approved address while TLS validates the original hostname.
Connection tests and delivery share these restrictions. HTTP redirects, environment proxies and cookies are disabled.
For a self-hosted internal Nextcloud server, allow only its required network/address; a tenant cannot add this exception.

## Camera ingress

Enable `ShutterSpool:CameraIngress:Enabled` when at least one deployment profile is active, as described in the [AppHost guide](../ShutterSpool.AppHost/README.md#camera-ingress). Configure `CallbackKey` as a separate secret of at least 32 characters, `CallbackBaseUrl` as an edge-reachable private URL ending in `/internal/camera`, and `InternalPort` as the private listener port. The main listener denies internal callbacks; the private listener denies management/media/web routes. Callback bodies, filenames, credentials and private paths are excluded from diagnostics/telemetry. Each profile has independent `Enabled`, `ControlPort` and (for FTPS) `CaRootPath` settings; all advertise the common `Hostname`.

Defaults are `MaximumUploadBytes=536870912` (512 MiB), `MaximumConcurrentTransfers=16`, `MaximumSessionsPerSource=2`, `UploadBandwidthKiBPerSecond=8192` and `TransferTimeoutSeconds=600`. `MaximumSessionsPerSource` is enforced independently by each enabled SFTPGo edge; a Source authenticates only through its selected profile. `MaximumConcurrentTransfers` is enforced by the central PostgreSQL admission path and remains shared across edges. Pre-upload admission checks the one-copy spool capacity envelope for the configured concurrency; an admitted transfer continues while writes remain possible. Set the payload reserve through `ShutterSpool:Spool:ReservedBytes` or `ReservedPercent` (bytes wins). Each edge's separate bounded pipe buffer is configured by AppHost. Set finite limits to match the host; configuration errors fail startup.

Create a Source with explicit `sourceType: "CameraFtpUpload"` and `connectionProfile: "ModernFtps"`, `"LegacyFtps"` or `"PlainFtpLan"` in the authorized `POST /management/sources` API. Requests missing either field or containing obsolete fields are rejected. `GET /management/source-capabilities` reports configured profiles and CA setup state. FTPS CA downloads use `/management/source-capabilities/{profile}/ca-root`; only public PEM is returned. Source configuration responses expose the selected type and profile. Creating or switching to a profile requires it to be enabled in deployment settings; an unchanged unavailable profile may be retained while editing general Source settings. A type/profile change increments `CredentialGeneration`, invalidating prior logins for new uploads. Trusted routes select the profile (`/internal/camera/auth`, `/legacy-auth` and `/plain-auth`), and auth, pre-upload admission and PostgreSQL recheck it; the camera cannot choose its own profile. All profiles use the same one-time username/secret.

The one-time username/secret provision an edge at authenticated login without using the SFTPGo console. `PUT /management/sources/{id}/ingress` with `{"expectedRevision":<current configRevision>,"enabled":false}` suspends new logins/uploads; `true` re-enables ingress. Regeneration and suspension advance the credential generation and invalidate old sessions for new uploads. A transfer admitted before that change may finish; existing curation/delivery work continues. `GET /management/ingress/transfers` returns authorized redacted transfer IDs, states, Asset IDs and failure reasons.

SFTPGo uses authenticated `POST /internal/camera/auth` and `/hook`, plus per-login HTTPFs endpoints.
HTTPFs accepts complete/truncating uploads without download/delete/rename/link operations.
The [acceptance sequence](../../docs/core-workflow.md#1-accept-responsibility-before-saying-success) explains byte receipt versus Asset commit; the [Infrastructure reference](../ShutterSpool.Infrastructure/README.md#camera-ingress-receive-and-recovery) owns recovery details.
Preserve uncertain bytes and consult transfer status before operator cleanup; original removal follows the [retention policy](../../docs/retention.md).

## HTTP and composition boundaries

HTTP DTOs stay beside their endpoints, with explicit Application mappings. Use the emitted OpenAPI document for exhaustive schemas; the [extension recipes](../../docs/architecture/system-boundaries.md#extension-recipes) explain how to add an operation.

Destination creation accepts implemented kinds `local` and `webdav`; an optional write-only `secret` is encrypted separately and never returned. WebDAV connection credentials are configured and replaced through its dedicated write-only endpoint. Source secrets are returned only on creation/regeneration. Error responses use redacted Problem Details; stale curation/action conflicts include authorized current state. Clients should regenerate from the current OpenAPI document, not serialize Domain or Application records directly.

Before the first startup of the initial migration baseline, provision a fresh application database and empty spool at separate locations. Preserve the original database/spool pair with its keys and configuration, shared PostgreSQL storage and identity-provider state. Follow [AppHost data setup](../ShutterSpool.AppHost/README.md#separate-application-data-for-the-initial-migration-baseline) for development or the [older-installation transition](../../deploy/operations/README.md#moving-from-an-older-pre-alpha-installation) for packaged deployments. Startup rejects historical schemas before recovery; it does not convert or delete existing data.
