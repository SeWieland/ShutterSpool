# ShutterSpool.ServiceDefaults

Shared health/OpenTelemetry defaults for actual .NET hosts; logs, traces, and metrics export through the configured OTLP endpoint, with no domain behavior. `/health/live` reports process liveness; `/health/ready` and `/health` report readiness checks, including PostgreSQL when the Server is configured with it. Cache, preview and telemetry availability do not gate readiness.

OTLP export is enabled only when `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` or `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` is explicitly configured.
With none configured, the host does not create an OTLP exporter or attempt the default localhost collector.
An unreachable optional exporter does not become an authoritative readiness dependency.

The Server filters live-hub requests and any request carrying an `access_token` query parameter from ASP.NET OpenTelemetry spans because SignalR bearer fallback may use the query string. Other HTTP routes retain normal tracing.

Sharing these defaults keeps host diagnostics consistent without making telemetry part of workflow correctness.
See [system boundaries](../../docs/architecture/system-boundaries.md) for that distinction.

## Build and checks
Build through `dotnet build ShutterSpool.slnx --no-restore` from the repository root; its behavior is covered by the Server liveness test.
