# Source layout

ShutterSpool is a modular monolith with six .NET projects and one Nuxt application. Feature folders describe implemented code.

| Project | Owns |
| --- | --- |
| [Domain](ShutterSpool.Domain/README.md) | Asset vocabulary and pure Capture grouping, curation, routing and delivery policies. |
| [Application](ShutterSpool.Application/README.md) | Use cases, workflow coordination, commands/results and necessary ports. |
| [Infrastructure](ShutterSpool.Infrastructure/README.md) | Internal PostgreSQL persistence, spool, secret protection, metadata, previews, Valkey and local/WebDAV delivery adapters. |
| [Server](ShutterSpool.Server/README.md) | Named HTTP DTOs/mapping, authorization, hosted loops and composition. |
| [AppHost](ShutterSpool.AppHost/README.md) | Development resources and configuration. |
| [ServiceDefaults](ShutterSpool.ServiceDefaults/README.md) | Health and telemetry defaults. |
| [Web](ShutterSpool.Web/README.md) | Nuxt presentation and the generated Kiota client. |

Domain has no adapter dependencies. Application references Domain; Infrastructure implements Application ports. Server uses Infrastructure only from Composition. HTTP DTOs live beside their endpoints in Server/Api; there is no separate Contracts project or automatic serialization of core models.

See [system boundaries and extension recipes](../docs/architecture/system-boundaries.md), [style](../docs/style.md). Component READMEs own runnable commands.
