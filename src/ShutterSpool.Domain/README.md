# ShutterSpool.Domain

Domain answers business questions without performing I/O: do these originals belong to one shot, which route matches, or has a lifetime boundary been reached?
Keeping decisions pure lets tests exercise them without a camera, database or provider.

It contains immutable Asset identity, conservative Capture grouping and timestamp compatibility, curation/Hold/Restore/release, typed routing values/operators/templates, delivery/reconciliation/completion policy, and accepted per-Asset retention lifetime/tier decisions.

Source protocol intent is explicit through `SourceType` and `SourceConnectionProfile`; the current type is `CameraFtpUpload`, with `ModernFtps`, `LegacyFtps` and `PlainFtpLan` connection profiles. Deployment availability is evaluated outside Domain.

Capture curation uses a signed rating (-1, 0, 1–5), derived selection, an optional fixed color label and individual text tags.
`CollectionName` validates portable names, optional nested segments and normalized case-insensitive identity; routing keeps the short name and full relative path as distinct facts.
Capture attention is a pure expression over named release/routing/delivery facts; Infrastructure supplies those facts and applies the expression in SQL before paging.

Domain references no other product project, EF Core, HTTP, JSON serializer, hosting or provider package. Policies accept typed snapshots plus explicit time/jitter and return decisions; adapters apply those decisions to their locked authoritative snapshots.

## Checks and further reading

Run `dotnet test --project tests/ShutterSpool.Core.Tests/ShutterSpool.Core.Tests.csproj --no-restore` from the repository root. [System boundaries](../../docs/architecture/system-boundaries.md) explains extension ownership.
