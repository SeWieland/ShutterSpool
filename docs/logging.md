# Application logging

Logs should help explain what the system did without becoming another private media catalog.
Stable event IDs make failures searchable across releases; structured fields let an operator correlate operations without exposing photographs or credentials.
Workflow state and deletion authority still belong in PostgreSQL, regardless of whether telemetry reached a collector.

Application code uses source-generated `LoggerMessage` methods for every new structured log event.
Keep generated methods close to their feature area, use Pascal-cased placeholders, and never include credentials, token material, private media locations, or source secrets.
Framework and third-party logs are outside this convention.

## Event ID ranges

| Range     | Owner                                                          |
| --------- | -------------------------------------------------------------- |
| 1000–1999 | Host startup, configuration, health, and operational lifecycle |
| 2000–2999 | Security, authentication, authorization, and credential audit  |
| 3000–3999 | Ingest, spool, and asset ownership                             |
| 4000–4999 | Capture grouping, metadata, and curation                       |
| 5000–5999 | Routing, delivery, retries, and destinations                   |
| 6000–6999 | Previews and media transformation                              |
| 7000–7999 | Management API, realtime, and user-facing workflow             |
| 8000–8999 | Protocol and provider integrations                             |
| 9000–9999 | Background recovery, retention, and migrations                 |

Reserve an unused ID in the owning range for each new message and give the generated method a stable descriptive name.
The event name defaults to that method name; specify `EventName` only when an external consumer requires a different stable name.
