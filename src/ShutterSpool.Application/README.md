# ShutterSpool.Application

Application coordinates the work: accepting originals, grouping Captures, releasing decisions, routing, delivery and retention.
It defines the ports those workflows need, while Domain supplies pure decisions and Infrastructure supplies durable storage and provider operations.
This lets the same ingest workflow serve a camera adapter without acquiring FTP session or filesystem assumptions.

Read [a photograph's journey](../../docs/core-workflow.md) for the behavior and [system boundaries](../../docs/architecture/system-boundaries.md) for the dependency rules.

## Camera transfer flow

`CameraIngressService` connects admission and source-event correlation to `IngestService`.
After credential verification, login initializes/checks the guarded virtual root and rechecks current authority before publishing success.
`SourceManagementService` applies deployment password requirements to generated/custom provisioning and revision-guarded replacement;
the persistence port atomically protects username uniqueness, configuration revision and credential generation.
Authentication carries the trusted connection profile selected by the callback route; camera-supplied FTP fields cannot choose it. A deployment profile withdrawal blocks new admission while an already admitted upload may still be received and completed.
The [acknowledgement sequence](../../docs/core-workflow.md#1-accept-responsibility-before-saying-success) explains when an upload becomes an Asset; the [Infrastructure reference](../ShutterSpool.Infrastructure/README.md#camera-ingress-receive-and-recovery) describes exact recovery states.
Camera paths are provenance, not spool keys.
An exact callback retry returns its recorded Asset; a new full upload keeps independent identity.

## Delivery contracts

[`IDestinationDeliveryAdapter`](Delivery/DeliveryContracts.cs) separates durable delivery work from a provider write and reconciliation.
The worker supplies a recorded target and a verified original stream; the adapter reports what its provider actually proved.
[`IDeliveryWorkSignal`](Delivery/DeliveryContracts.cs) only wakes the worker and never replaces durable claims or retry state.
See the [destination boundary reference](../ShutterSpool.Infrastructure/README.md#destination-delivery-boundaries).

## Coordination and ports

| Responsibility | Starting point |
| --- | --- |
| Acceptance and source correlation | `IngestService`, `CameraIngressService` |
| Bounded Capture reads and revisioned curation | `CaptureReadService`, Capture read/curation ports |
| Collection management and versioned single/bulk Capture moves | `CollectionManagementService`, `CaptureCollectionAssignmentService` and their stores |
| Delivery claims, verified originals and provider dispatch | `DeliveryProcessingService` |
| Explicit routing actions and supplier verification | `CaptureRoutingActionService` |
| On-demand previews and durable prewarm work | `PreviewService`, `PreviewPrewarmService` |
| Favorite, Trash/Restore/Resume, capacity and settings | Retention services and their stores |

Application results are not HTTP contracts; endpoint-local DTOs belong in Server.
No EF Core, HTTP framework or provider client belongs here.
`TimeProvider` supplies workflow time so decisions can be exercised at exact boundaries.

### Implementation constraints

`Spool/ICommittedOriginalReader` returns streamed originals with opaque keys.
Consumers cannot require filesystem paths or seekability; adapters own any bounded materialization and cleanup.
The validated camera admission may use an internal size-bounded ingest/spool continuation without repeating the reserve decision; actual write errors and upload limits still apply.

Capture reads expose bounded pages and detail projections, not entire delivery histories.
Collection operations change metadata through their ports, without original-byte reads or provider calls.
Grouping chooses the Source default for a new Capture; release freezes Collection context and automatic late-Asset routing reuses it.
Explicit Reevaluate uses current Collection facts, while Retry/Resend retain recorded targets.
Preview size overrides keep the validated, size-aware cache identity; calendar target aliases use only trustworthy recorded Capture time, without device-timezone conversion.
Source-authentication and preview-activity observations are advisory and cannot delay acceptance or establish readiness.

Retention callers use the server-authored Capture summary rather than recalculating eligibility.
`RecordNoDeliveryIntentAsync` requires all current members routed and no matched decision or Delivery history; new matched work clears the disposition atomically without erasing prior obligations.
Cleanup requires durable removal ownership and the original-use fence; deadlines and unavailable storage alone never establish completion.

For lock/save ordering, use the [persistence reference](../../docs/architecture/persistence.md).
For runnable checks, use the [test guide](../../tests/README.md); Application has no separate host to start.
