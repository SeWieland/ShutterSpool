# ShutterSpool.Infrastructure

Infrastructure connects workflows to PostgreSQL, original storage, caches, media tools and destination providers.
It implements Application ports and keeps EF entities/context and provider clients internal.
An adapter reports what storage or a provider proved; it does not invent a workflow outcome to hide an uncertain operation.

Read the [architecture](../../docs/architecture/system-boundaries.md) and [core workflow](../../docs/core-workflow.md) first.
The sections below are an implementation and operations reference for changing those adapters safely.
For transaction/lock ordering, use the [persistence reference](../../docs/architecture/persistence.md).

## Retention persistence and cleanup

Automatic cleanup holds a dedicated PostgreSQL session advisory lease for the full bounded pass. A competing replica skips that pass; per-Asset original-use fences still protect physical removal. Pressure episodes continue to the recovery target, and only finite pending reclamation reduces further selection; protected occupancy remains in admission accounting.

PostgreSQL stores the Capture disposition, per-Asset favorite-loss clock, policy revision, operation ownership and exact removal identity. Bounded classification persists each Active Capture's most protective tier and common pressure/ordinary boundaries; the worker completes a bounded dirty-facts pass before selecting from a globally tier-and-boundary-ordered index. Every candidate is re-evaluated under routing-then-Capture locks before Active-to-Trash commits. Quota inventory remains authoritative for physical bytes, so moving a Capture to Trash does not count as reclaimed capacity.

Cleanup closes same-process preview use, takes the exact Asset/storage-key PostgreSQL exclusive fence, removes exact derivatives and authorized original/transient keys, then records physical progress. The filesystem adapter validates the root custody marker, key-to-Asset identity, expected original size and reparse/path boundaries. Failed or unavailable surfaces remain pending; an expired worker lease alone never proves I/O has stopped. See the [retention guide](../../docs/retention.md) for lifetimes, protections and removal constraints.

## Durable ingest

`DatabaseInitializer` holds a PostgreSQL session advisory lock while checking schema compatibility, applying migrations and completing startup recovery, so concurrent Server processes cannot race startup changes or recovery. It explicitly releases the advisory lock before returning the pooled connection.

`IngestService` creates an operation before streaming. Camera ingress, the currently wired transport, writes once under a journaled, generated `spool/` key with a 64 KiB buffer and incremental SHA-256/size. It preserves asynchronous write-through and disk flush before recording the receipt. Finalization publishes the Asset in PostgreSQL after the caller establishes the matching successful source event; it does not copy or reread the payload.

The [acceptance sequence](../../docs/core-workflow.md#1-accept-responsibility-before-saying-success) explains the camera-facing promise and its early-EOF limit.
Filesystem writes and PostgreSQL commits are separate durable steps; only the Asset commit permits acknowledgement.
Live reads derive transfer/Asset/Capture identity from PostgreSQL; preview readiness additionally requires valid cache output for the current primary JPEG and requested dimensions.
A prewarm row or transient activity hint alone cannot prove readiness.

The separate [`IngestService.AcceptAsync`](../ShutterSpool.Application/Ingest/IngestService.cs) API remains implemented and tested, but has no current transport caller. It records `Receiving`, writes and flushes an `incoming/` partial, copies and flushes the `spool/` original, records `Promoted`, then saves `Committed` and the `Spooled` Asset together before returning success. The partial remains as recovery evidence even after acceptance. Camera ingress does not use this partial/copy path.

Ordinary recovery verifies journaled size/hash, or requires a discovered final file and matching partial when the receipt was not saved. A known final key can be restored from a verifiable partial; an isolated partial without a known or discoverable final key does not prove completion. Unverifiable bytes are quarantined. Camera recovery instead requires the successful source correlation described below.

| Operation state | Meaning |
| --- | --- |
| `Receiving` | Journal row exists; a partial file may be incomplete. No Asset has been accepted. |
| `Promoted` | Final key, byte size and hash are journaled; Asset creation is still pending. |
| `AwaitingSource` | A generated private receive key is journaled; bytes may be incomplete. Generic recovery cannot publish it. |
| `Received` | Bytes are flushed and size/hash are journaled, but successful source completion must still be established before Asset publication. |
| `Committed` | The operation and `Spooled` Asset were saved together; the source may be acknowledged. |
| `Quarantined` | Recovery could not prove the bytes; tracked operations retain a database reason. A previously committed Asset is also marked `Quarantined` if its bytes cannot be recovered. |

The spool root may contain these directories. A file's location alone does not establish acceptance; PostgreSQL owns that state.

| Directory | Contents |
| --- | --- |
| `incoming/` | Retained `AcceptAsync` partials; camera receive creates none. |
| `spool/` | Accepted originals and private camera receive candidates, including incomplete or unaccepted uploads. |
| `quarantine/` | Retained files with a `.reason` sidecar. |
| `.camera-directories/` | Empty camera virtual folders; no media payloads. |

An untracked quarantined file has no operation row; its sidecar is its operator-visible explanation. Startup recovery supplies an explicit fallback reason if it finds a quarantined file whose sidecar was lost during an interrupted move. Inspect the file and reason before deciding how to handle it; ingest does not delete or release quarantined bytes. A reason sidecar without a matching file can remain after an interrupted move.

Configure `ShutterSpool:Spool:RootPath` and either `ReservedBytes` or `ReservedPercent`; the byte reserve wins when both are set. Use a private, single local filesystem root owned by the service, with no untrusted local writers or reparse points in its path. Durability depends on the selected volume preserving flushed files and parent-directory metadata; abrupt power loss remains volume-specific evidence, not a camera compatibility claim. See [AppHost setup](../ShutterSpool.AppHost/README.md) for local resources.

From the repository root, run `dotnet build ShutterSpool.slnx --no-restore` and `dotnet test --project tests/ShutterSpool.IntegrationTests/ShutterSpool.IntegrationTests.csproj --no-build` after restoring/building. PostgreSQL cases are mandatory in CI and require a connection locally: set `SHUTTERSPOOL_INGEST_TEST_CONNECTION` to a local PostgreSQL admin connection; each creates and drops its own disposable database. Synthetic local media is used throughout.

## Camera ingress receive and recovery

`EfCameraIngressStore` owns login credential generations, serialized upload admission and transfer outcome/recovery correlation in PostgreSQL. A login records the trusted profile selected by its private auth route; FTP request fields cannot select it. Login creation and every upload admission recheck the Source type, profile and current credential generation under the credential-then-Source lock order. Changing type/profile advances the generation, so old logins cannot begin a later upload; Server also checks deployment profile availability at pre-upload admission. An already admitted upload can still be received and completed after withdrawal. HTTPFs reaches source-neutral spool receive through Server; neither edge has a spool mount. Camera virtual paths supply provenance and directory checks, never spool keys. File and empty-folder metadata is scoped to stable Source identity, so reconnects see the latest virtual listing without crossing Sources. `FileSystemCameraIngressDirectories` creates and lists empty virtual folders under `.camera-directories` in the spool root; file metadata comes from PostgreSQL and this adapter never reads or writes media. Callback replay returns the recorded result; each new full upload receives an independent Asset.

`EfSourceAuthenticationObservationStore` retains one dated outcome and increasing database version per Source. Its conditional upsert fences observations with the original UTC ticks (PostgreSQL timestamps are only microsecond-precise), rejects older observations, and its foreign key removes snapshots with a deleted Source; these rows do not change configuration revisions or authorize ingest.
`EfSourceDisplayStore` projects only Source identity, curation policy, revision and archive state for the Server's disposable local display cache. It never returns credential usernames, generations, verifiers or secrets; configuration editor and workflow reads continue using authoritative stores.

The exact ingest custody states are listed [above](#durable-ingest).
`CameraIngressService.RecoverAsync` reconciles the recorded source outcome with `IngestService.RecoverAdmittedAsync`.
Only a matching successful admission marker permits verified receive bytes to become an Asset; interrupted or unverifiable receives remain private/quarantined with a reason.
Recovery may reread bytes to verify custody; normal finalization does not.
The [recovery overview](../../docs/core-workflow.md#5-recover-without-inventing-a-happy-ending) connects this to delivery and removal recovery.

Retain spool and PostgreSQL state across restarts. Generic recovery skips camera receive-only operations; camera recovery requires successful correlation before publication. The ordinary `AcceptAsync` partial/final operations keep their recovery rules. Failed edge cleanup is denied so uncertain handoffs keep their evidence; ingress recovery itself does not authorize original deletion; the [retention policy](../../docs/retention.md) governs that action. See [Server settings](../ShutterSpool.Server/README.md) and [AppHost wiring](../ShutterSpool.AppHost/README.md#camera-ingress). The [camera guide](../../docs/camera-compatibility.md) describes hardware observations and the clean-early-EOF limitation.

## Background metadata

The Server scans PostgreSQL for accepted Assets, claims one at a time with a five-minute lease, and retries transient failures with a bounded backoff and five-attempt limit. Parsing reads the committed original through the spool reader and never holds up ingest acknowledgement. JPEG and Sony ARW facts include detected role, embedded original capture time with its offset and precision when present, and embedded camera identity. Unknown zones remain local camera time. Originals are never modified. Files above 512 MiB and unsupported content receive a terminal outcome. Metadata success is not a requirement for durable acceptance.

## Capture grouping

The Server scans for Assets with terminal metadata outcomes and persists one Capture membership per Asset in PostgreSQL. It groups RAW/JPEG complements and distinct OTHER variants only with a unique, compatible embedded-shot match, records recognized replays as related non-primary Assets, and leaves uncertain or competing Assets separate. Sony RAW image SubIFDs and ordinary TIFF EXIF retain camera/time evidence; contradictory serials make that evidence uncertain. Grouping neither reads original bytes nor waits for previews or a pair timer. Matching combines variant role, embedded camera identity and compatible timestamp evidence; insufficient or contradictory evidence leaves Assets separate. Existing memberships and delivery histories are not automatically rewritten.

## Capture previews

The preview adapter resolves the current primary JPEG through Capture membership, reads only its committed original through the spool boundary, and POSTs bounded bytes to private Imaginary. WebP files in the configured cache are disposable and keyed by Capture, primary JPEG Asset, profile and dimensions. Grouping records durable prewarm work for a primary JPEG; a separate Server worker claims it with a PostgreSQL lease and retries temporary failures. See [Server configuration](../ShutterSpool.Server/README.md#private-capture-previews) for operator settings.

`EfPreviewSourceResolver`, `FileSystemPreviewCache` and `ImaginaryPreviewGenerator` implement separate Application ports; `PreviewPrewarmService` owns coordination. Metadata and preview adapters materialize non-seekable input to bounded private temporary files and clean up on disposal, cancellation and failure.

## Destination delivery boundaries

PostgreSQL owns delivery claims, attempts and completion; the provider adapter owns publication and reconciliation evidence.
The [workflow guide](../../docs/core-workflow.md#4-route-a-decision-then-deliver-with-evidence) explains that split.

| Responsibility | Implementation entry point |
| --- | --- |
| Hosting scan/wakeup loop | Server `CaptureDeliveryWorker` |
| Atomic claims, renewal and fenced completion | `EfDeliveryWorkProcessor` with independent contexts |
| Original verification and provider dispatch | Application `DeliveryProcessingService` and `IDestinationDeliveryAdapter` |
| Local publication and mount identity | `LocalDestinationDeliveryAdapter`, `LocalFileDelivery` |
| WebDAV publication and reconciliation | `WebDavDestinationDeliveryAdapter` |
| Provider configuration and connection tests | Local/WebDAV configuration implementations |

Composition binds internal implementations and rejects duplicate Destination-kind registrations at startup.
Unsupported configuration produces visible failed/uncertain work rather than disappearing from the scan.
WebDAV uses HTTPS Basic with normal TLS validation and no redirects; its collection/account identity locks once work exists, and replacement credentials remain write-only and encrypted.
Provider response bodies never cross the adapter boundary.
WebDAV ambiguity stays blocked until final-target receipt or read-back evidence can be verified; a missing target alone never permits another write.

### WebDAV evidence and cleanup constraints

WebDAV runs an isolated no-replace capability probe before publishing a new target. Media PUT sends the known Content-Length and requests `X-Hash: sha256`; any returned `X-Hash-SHA256` must be well-formed and match the original before MOVE. Completed PUT/MOVE responses confirm normal delivery with `AcknowledgedCompleteWrite` evidence and no final media download; HTTP 202 acceptance cannot confirm completion.
Media PUT also sends `OC-Checksum: SHA256:<Asset hash>`. Occupied-target and uncertainty checks request final-resource checksum and byte count through bounded Depth: 0 PROPFIND. Matching stored SHA-256/size confirms with `ProviderReceipt`, explicitly trusting the provider's receipt even when its checksum was supplied by a client. It is not independent stored-byte verification. Mismatching evidence preserves the final resource as a conflict; absent usable checksums permit complete HTTP 200 read-back with size/SHA-256 validation. Recovery metadata failures remain uncertain; definite transient pre-publication outages retain backoff. Partial responses, filenames, size and ETags alone cannot confirm success.
Requests have a ten-minute deadline, original/read-back buffers are 64 KiB and Depth: 0 XML is capped at 64 KiB with DTDs disabled.
Provider HTTP logging and exported URL traces are suppressed so account paths and original filenames remain private.
Probe cleanup requires both verified synthetic bytes and a strong ETag supplied with conditional DELETE; changed, unverifiable or potentially active resources are retained.
Uncertain requests can leave attempt-named resources beneath `.shutterspool-staging` or `.shutterspool-probe-{random}.bin` resources in the configured collection.
There is no staging sweep: identify owned attempt/probe resources and establish that all corresponding provider operations have finished before operator cleanup.
Never delete final delivery targets as staging cleanup. Inconclusive reconciliation remains durably AMBIGUOUS and blocks automatic writes; a late worker's success cannot bypass its expired database claim.

### Tested WebDAV profile

Controlled compatibility checks used Nextcloud **31.0.9** behind nginx **1.28.0-alpine**, on loopback HTTPS with normal certificate/hostname validation.
The pinned images were `nextcloud@sha256:fdab7bfc1b35255e442ed207bca277a45495fb115f297a97711e0880167f833b` and `nginx@sha256:30f1c0d78e0ad60901648be663a710bdadf19e4c10ac6782c235200619158284`.
The provider used SQLite metadata, a private Docker network and a dedicated Linux Docker Desktop named volume retained across provider stop/start.
The proxy allowed 128 MiB requests with request buffering disabled, a two-second upstream connection timeout and a 120-second read timeout.
The largest exercised original was 16 MiB; neither the larger proxy allowance nor successful acknowledgement establishes support for larger media or storage power loss.
Checks covered nested/Unicode paths, independent final hashes, occupied-target preservation, outage/restart, lost-MOVE acknowledgement recovery through stored checksum receipts, and normal delivery without final-media download.
These observations do not establish compatibility with every WebDAV server, proxy or external storage system.
See the [controlled Nextcloud checks](../../tests/README.md#controlled-nextcloud-delivery-checks) for opt-in dependencies and custody.

## Persistence and bounded reads

The single internal `ShutterSpoolDbContext` owns configuration, grants and workflow state. `DatabaseInitializer` applies known pending migrations before recovery/exposure; historical or unknown migration histories, nonempty unversioned databases and nonempty spools for fresh databases remain rejected. See [atomic operations](../../docs/architecture/persistence.md#persistence-and-atomic-operations) and [extension recipes](../../docs/architecture/system-boundaries.md#extension-recipes). Provider guarantees depend on the actual storage/mount semantics; test each deployment mount before using it for originals.

The initial migration `20261025211318_InitialCreate` creates the current schema and seeds the fixed built-in Collection once. Source/Capture Collection defaults and restrictive foreign keys preserve that identity. Nullable release/decision Collection fields represent unrecorded context; initialization does not inspect originals or enqueue deliveries.
The SQL-owned `deployment_key_checks` singleton and the one-time Collection, quota and retention seeds are intentionally outside the EF entity snapshot. Database defaults and physical index names are mapped explicitly, including partial uniqueness for RAW/JPEG primaries and retention-selection indexes.
Direct EF migration tooling also refuses existing schema/history and downgrade, but it cannot validate spool custody. Use Server startup for installations; the runtime checks database/spool compatibility under the startup lock before migration and recovery.
See the [older-installation transition](../../deploy/operations/README.md#moving-from-an-older-pre-alpha-installation) for separate fresh storage and retained keys/identity. No historical chain is a supported upgrade predecessor.
`EfCollectionManagementStore` owns bounded search and guarded rename/delete; `EfCaptureCollectionAssignmentStore` commits versioned moves independently per Capture.
Source defaults, grouping, moves, release, Reevaluate and erasure share the [Collection-assignment fence](../../docs/architecture/persistence.md#persistence-and-atomic-operations).

`CaptureReadStore` reads stable first-acceptance Capture keyset pages, previews at most eight grouped Asset identities per inbox item, and reports the full variant count. Delivery/routing summaries are SQL aggregates, so growing delivery history is never materialized for an inbox page. Detail reads expose extracted metadata and current Source policy facts through Application projections; neither reads spool keys, hashes nor original bytes.
Collection-ID filters apply before paging; current and frozen release Collection projections remain separate.

Configuration removal preserves referenced history through archival. Source removal holds the credential/Source locks used by
admission; Destination removal holds the global routing/configuration locks. Related authoritative media/work/history
forces archival, while unused configuration and its own secret/provider/rules can be deleted atomically. Archived
rows remain available to existing Capture/delivery reads but are excluded from active configuration and reject edits.
Archival removes no original spool bytes or provider files. Destination archival pauses
Pending/RetryWait claims and preserves factual completion/reconciliation of existing I/O.
Frozen release snapshots also force archival before routing has created decisions/work. New snapshots omit archived
Destinations; old snapshots remain unchanged and can create Disabled work against the retained configuration.
Attention filtering projects named release/routing/delivery facts and applies `CaptureAttentionPolicy.RequiresAttention` from Domain in SQL before paging.

## Suppression evidence and supported restore

`EfPrivacyErasureStore` owns local erasure authorization, redaction and progress.
`PrivacyErasureSuppressionPersistence` owns paged export/import using the same scoped database context;
reconciliation verifies the complete manifest and every existing identity before applying any page.

Privacy erasure retains minimal Asset/Capture/operation identities and generated storage keys in independent suppression rows. Its export contains an authenticated, encrypted manifest and bounded pages, protected with the existing deployment encryption key. Keep that key and the **current** snapshot identity and complete exported package outside any application backup being restored; the [operator maintenance guide](../../deploy/operations/README.md) owns the executable export/custody and supported restore procedure. Supported recovery requires no privacy erasure since the recovery point; recovery predating erasure or with uncertain erasure history remains unsupported, even though the suppression guards below remain implemented.

For an explicit restore, set `ShutterSpool:Restore:Enabled=true`, `ShutterSpool:Restore:SuppressionSnapshotId` to that externally retained current identity, and `ShutterSpool:Restore:EvidenceDirectory` to the complete package directory. Save the returned manifest as `manifest.json` and pages as `page-000000.json`, `page-000001.json`, and so on. Startup validates every page and reconciles suppression before ingest recovery or HTTP exposure. Missing, invalid, mismatched or incomplete evidence fails startup. Pending local erasure must finish before exposure; unresolved preservation keeps restore blocked.

Ordinary startup cannot detect an arbitrary database rollback or prove an operator supplied the newest snapshot. A restore that omits current evidence is unsupported and cannot claim suppression. These records do not prohibit a new ingest, erase downstream copies, provision backups or establish secure disk wiping.

Ordinary ingest recovery preserves suppression-authorized original and known transient keys during orphan reconciliation. A crash after metadata redaction cannot relocate those bytes into untracked quarantine; the bounded erasure retry retains their exact removal authority.
