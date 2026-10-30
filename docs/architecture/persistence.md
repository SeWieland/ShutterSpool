# Persistence and recovery reference

Read the [architecture guide](system-boundaries.md) and [core workflow](../core-workflow.md) first.
This reference is for changing operations that cross database, filesystem or provider boundaries.
The details here protect accepted media when an operation races another worker or stops between two durable writes.

## Persistence and atomic operations

Infrastructure owns one internal `ShutterSpoolDbContext` and migration history. Source identity/policy is independent from its ingress credential; regeneration changes the verifier and generation, while ingress suspension changes enabled state and generation. Identity, root, historical Assets and existing workflow state remain intact. Destination identity has an immutable provider kind and enabled state; routing rules, local root configuration and optional encrypted credentials have separate destination-owned records. Authorization grants use issuer/subject, not source or destination identity.

In shared-workspace mode, this same schema and its database-local locks live separately in each tenant database.
The scoped context factory selects the verified workspace's runtime connection reference; it never changes a global connection while work is running.
The separate control registry owns routing/provisioning and allocation metadata, while grants and workflow mutations remain tenant-local.
Provisioning and upgrades use separate migration privileges through the [operator commands](../../src/ShutterSpool.Server/README.md#shared-workspace-provisioning).
Normal shared-workspace startup checks the exact supported schema and storage identity and completes recovery before admitting tenant work; it does not migrate or repeat the initial grant.
It holds an exclusive writer-lock file in each allocated root for its lifetime; operator upgrade/allocation commands acquire the same locks before changing state.
Fixed filesystem allocations preserve tenant-local quota reservations without cross-database upload accounting.

Memberships add a fixed role and revision to the issuer/subject grant; Viewer policy and access audit/operation results stay in the same tenant database.
The workspace-role migration upgrades prior administrative grants to Owner while retaining their active/revoked state.
Apply it through the existing explicit shared-workspace upgrade procedure; it does not adopt an incompatible historical installation.
Access changes serialize current actor/target/revision/last-Owner checks and their audit in one short transaction.
Ordinary workflow commands use fresh request/item admission and retain their existing feature transactions; admitted work may commit after revocation.
See [authorization](authentication-and-authorization.md#roles-and-access-changes) for that boundary and the current media predicate.

Recipient references and workspace offers are tenant-local. Offer transitions reuse the access-change lock, authority checks and operation/audit storage; identity-provider operations are separate commits.
Acceptance reads PostgreSQL's current clock after acquiring the lock, then commits membership and accepted state together. Renewal advances an unaccepted offer's revision/lifetime; accepted membership is never overwritten by a retry.
Membership changes invalidate affected pending offers in their existing transaction, including removal and exceptional principal replacement. See the [invitation API contract](../../src/ShutterSpool.Server/README.md#workspace-invitations).

Camera ingress keeps login/admission records separate from Sources and Assets.
Admission checks the login's credential generation under the credential/Source locks; an already admitted transfer may finish after revocation.
Each Source selects one explicit `ConnectionProfile`; a camera login records the profile supplied by its trusted callback route.
Changing the Source profile increments credential generation without changing the secret. Login and admission require the recorded profile to match the Source's current selection.
Publication requires durable successful source correlation, not EOF or a file listing.
Generic recovery skips camera receive-only operations; the [Infrastructure reference](../../src/ShutterSpool.Infrastructure/README.md#camera-ingress-receive-and-recovery) owns their exact custody/recovery states.
The [acceptance sequence](../core-workflow.md#1-accept-responsibility-before-saying-success) explains why this differs from byte receipt.
SFTPGo has no spool mount; virtual directory operations never own media payloads.

An internal, size-bounded spool continuation lets the admitted Application workflow finish without repeating the reserve decision; actual filesystem failures still propagate. Application grants Infrastructure access to this internal adapter port, while ordinary ingest callers retain admission checks.

The initial migration baseline requires a fresh application database and empty spool at separately selected locations when moving from an incompatible older installation.
Incompatible existing state is rejected before schema or data changes. This does not authorize deleting or reusing the original database/spool.
See [AppHost data setup](../../src/ShutterSpool.AppHost/README.md#separate-application-data-for-the-initial-migration-baseline); retain original configuration/keys, shared PostgreSQL storage and identity-provider state for the documented rollback boundary.

Lock order is explicit: operations needing both advisory locks take `shutterspool-routing` before `shutterspool-collection-assignment`, then acquire their row locks.
Grouping takes both keys before its claimed Asset, source/stem advisory key and Capture rows; it reads the Source default under a shared Source lock.
Curation mutations take routing before the Capture row; release also takes the Collection-assignment key before reading the Collection snapshot.
Collection rename/delete and Capture moves take the assignment key before reading Collection state; moves then lock the Capture row.
Collection lookups and bounded reference counts use EF queries; the assignment key serializes Collection rename/delete with reference admission.
Source creation/default updates take the assignment key; Source updates retain credential-before-Source row ordering used by admission.
Destination edits, claims, renewals, completion and routing actions serialize through the routing lock.
Reevaluate and privacy erasure also use the assignment key when reading or changing Collection context.
Configuration revisions share these locks with their writes; provider connection tests read a revisioned snapshot and perform provider I/O after the read.
Never introduce a reverse Capture→routing or assignment→routing dependency.

- Grouping evaluates Domain policy against locked evidence and atomically records Asset membership, the new Capture's Source-selected Collection, policy and prewarm work. Late attachments retain the Capture's membership.
- Release freezes curation, Collection ID/name/path and the complete routing configuration under the same transaction.
- Collection deletion checks all Source/Capture references under the assignment fence and restrictive foreign keys; bounded displayed counts cannot authorize deletion. Moves commit independently per explicitly selected Capture and preserve release/delivery history.
- Routing evaluates frozen configuration and atomically records decisions, target reservations and deliveries.
- Capture browsing derives attention facts from authoritative rows and applies the Domain attention predicate and exact-ID Collection filters before SQL paging; Domain has no persistence types or query-provider dependency.
- Retry/Resend/Reevaluate verify original/replay streams before opening the action transaction, then recheck authoritative supplier membership, decision state and confirmation freshness under locks.
- Reevaluate saves changed decisions/work before querying completion; both saves are inside the transaction. Delivery completion excludes the completing delivery from pending-work queries before its final save. Preserve these save points when changing queries.
- Provider transfers run outside transactions. Each delivery claim, renewal and completion gets an independent context; the claim token and expiry fence completion in PostgreSQL.

No generic repository or universal transaction abstraction replaces these feature operations. Valkey wakeups and preview availability never determine durable work or acceptance.

## Privacy erasure and restore evidence

The Application automatic-retention pass requires a controller lease port. Infrastructure pins its PostgreSQL session advisory lease across planning and transitions, preventing replicas from independently spending the same pressure deficit. Original reads/removal retain their separate exact Asset fences.

Application coordinates separately confirmed finite erasure through retention/protection, preview-drain and exact-original-removal ports. Infrastructure retains minimal independent suppression rows, authenticated paged exports and complete-import evidence; stale workflow/recovery reads must consult suppression before publication. Server owns explicit HTTP DTOs and management authorization. Restore composition validates externally current evidence and drains pending local erasure before ingest recovery, hosted workers or HTTP exposure. The current manifest identity and deployment encryption key require custody outside the rolled-back database. Supported recovery is limited to a complete consistent backup with no erasure since the recovery point; uncertain erasure history is unsupported. Ordinary startup cannot certify an arbitrary rollback with lost evidence.

Local erasure and suppression-package persistence share the scoped EF context behind the Application port.
Keep one transaction owner within each operation.
Erasure clears copied Collection facts in release/routing history and resets the retained redacted Capture's membership to the built-in Collection; it does not erase shared Collection configuration.

For policy and recovery promises, use the [retention guide](../retention.md).
For spool states, provider evidence and explicit restore configuration, use the [Infrastructure reference](../../src/ShutterSpool.Infrastructure/README.md).
