# Working on ShutterSpool

ShutterSpool receives camera media, keeps accepted originals recoverable, supports optional curation, and delivers originals to configured destinations.
Read [CONTRIBUTING.md](CONTRIBUTING.md), [code style](docs/style.md), and the relevant component guide before changing code.
For interface work, also read [PRODUCT.md](PRODUCT.md), [DESIGN.md](DESIGN.md), and [UI guidance](docs/ui.md).

## Preserve the product boundaries

- Acknowledge ingest only after bytes and authoritative Asset state are durable and jointly recoverable. Grouping, previews, curation, routing and delivery follow acceptance.
- PostgreSQL owns workflow truth; the spool owns immutable originals. Valkey, previews and telemetry are disposable.
- Stream media with bounded buffers. Every accepted upload keeps independent Asset identity; uncertain grouping never discards media.
- Curation precedes routing. Delivery is at least once, with explicit idempotency, reconciliation and conflict handling.
- Retention needs explicit policy and exact removal authority. Empty delivery lists, unavailable caches, lost telemetry and expired worker leases never prove deletion is safe.
- Keep credentials and private media out of logs, fixtures, screenshots, external analytics and agent context. Use synthetic media and controlled endpoints for routine checks.
- Preserve server authorization, certificate validation, authentication settings and persistent keys. Report an access restriction instead of weakening it.

## Keep changes within their owning layer

Start with the [source map](src/README.md) and [system boundaries](docs/architecture/system-boundaries.md).
Domain contains vocabulary and pure policies; Application contains use cases and ports; Infrastructure contains internal persistence and adapters.
Server owns endpoint-local DTOs, authorization and hosting; only Composition references Infrastructure.
Generate the frontend client from OpenAPI rather than exposing core or persistence models as transport contracts.
Tests belong to Core.Tests, IntegrationTests or ArchitectureTests, alongside the Web checks described in the [test guide](tests/README.md).
Add folders for implemented behavior, without empty scaffolding or generic repositories.
Update the owning component guide and architecture guidance when a boundary changes.

## Work and verify

Inspect the workspace first and preserve unrelated edits. Keep a coherent change on one branch; use worktrees when concurrency or checkout conflicts require them.
Resolve ordinary implementation details locally; ask about material product, contract, security or data-loss decisions.
Verify changed behavior against the request and relevant safety constraints, with independent review for substantive changes.
Tests provide evidence rather than defining the requirement; retain meaningful failure and recovery cases.
Review documentation for accuracy and check local links before completion.

Use the [AppHost lifecycle commands](src/ShutterSpool.AppHost/README.md#start-the-development-environment): `aspire start --non-interactive`, then `aspire wait` for required resources.
Use Aspire to stop/restart resources; do not terminate unrelated processes or reinstall tooling to work around a restriction.
Publishing, production changes, destructive operations and external communications require explicit authorization.
