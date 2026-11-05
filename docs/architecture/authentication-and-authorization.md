# Authentication and authorization

A valid sign-in and permission to use this deployment are separate decisions.
That lets an operator revoke local access without waiting for every previously issued token to expire.
The [Server reference](../../src/ShutterSpool.Server/README.md#local-security-configuration) owns concrete settings; this guide explains the implemented trust boundary.

## The short version

- An external OpenID Connect (OIDC) issuer authenticates a person. ShutterSpool does not store or check management passwords.
- The Server validates the issuer, audience, signature and expiry. Provider roles do not grant workspace access.
- The Server then checks an active local grant in PostgreSQL, keyed by the token's stable issuer and subject. Both checks must pass before a management API, private preview/original, or live-status stream can return private data.
- PostgreSQL is the authority for grants and credential state. Valkey is not in the authorization path and cannot make stale or revoked access valid.

```mermaid
flowchart TD
    Client[Client with bearer token] --> Token{Token valid?}
    Token -->|No| Deny[Deny private access]
    Token -->|Yes| Grant{Active grant?}
    DB[(PostgreSQL)] --> Grant
    Grant -->|No or unavailable| Deny
    Grant -->|Yes| Data[Authorized private response]
```

The web client is outside the trusted authorization boundary. It may hide or show controls, but it cannot authorize access; the Server does that for every protected request.

## Why there are two decisions

OIDC answers **who presented this token**, while a local grant answers **whether this person may currently access the selected workspace**. Keeping those separate makes revocation and recovery local, without making ShutterSpool a password store or coupling media/workflow ownership to an identity-provider database.

| Decision | Authority | What it proves | Failure result |
| --- | --- | --- | --- |
| Authentication | Configured OIDC issuer, verified by Server | A token is from the allowed issuer, for this audience and unexpired. | No principal; protected endpoint returns no private data. |
| Authorization | PostgreSQL, checked by Server | The `(issuer, subject)` has an active ShutterSpool grant. | Access is denied, including if the database cannot make a safe decision. |

Keycloak is an identity provider here, not the owner of photographic permissions. It does not contain individual Assets, Captures, Routes, Destinations, or Delivery permissions. Those records and workspace grants remain ShutterSpool workflow truth in PostgreSQL.

## Protected request and revocation

Every protected HTTP request goes through both checks. A valid token alone is insufficient. This means an operator can be removed by deactivating the local grant even while the token would otherwise remain valid.

Signing metadata and keys come from the configured issuer as needed; token validation does not mean asking the issuer to authorize each photograph.
The Server verifies signature, issuer, audience and expiry, then reads the selected workspace's local grant before returning private data.

Live status is a long-lived exception only in transport, not in policy. The Server closes the hub connection at token expiry and rechecks its local grant every 30 seconds; a revoked principal therefore loses hub access at the next check. The legacy live-status event stream checks expiry and authorization every 60 seconds. A cache miss, stale cache entry, or lost cache invalidation notification must never preserve access.

## Bootstrap and recovery

[Explicit workspace provisioning](../../deploy/operations/README.md#workspace-provisioning) creates the first local grant once, against a verified issuer/subject.
An ordinary restart never replays that grant or substitutes the deployment operator as a workspace member.
There is no default password, automatic first-login enrollment or unauthenticated recovery endpoint.

For an operator without an existing issuer, the supported profile supplies a Keycloak companion. Deployment secrets create its operational bootstrap and initial ShutterSpool administrator; versioned realm, client and audience-mapper configuration is reconciled without requiring a Keycloak-console visit. Keycloak’s own master administrator is deployment plumbing, not a ShutterSpool login.

The companion preserves the explicitly configured subject when provisioning a fresh administrator. For an existing exact username, reconciliation verifies its Keycloak ID against that subject and fails before account changes if they differ. Correcting initial account setup requires the verified intended subject; it never replaces the existing account or changes an already provisioned workspace grant.

## Workspace selection

A verified identity may discover only workspaces where it has a current local grant.
When discovery is complete and exactly one workspace is accessible, a page without a selection opens it automatically. Explicit invalid/unavailable selections and incomplete discovery never trigger a fallback.
The URL selects the workspace for that browser page, and each API request carries its explicit workspace identity.
The Server resolves the tenant before checking that tenant's local grant; missing or invalid selection never falls back to another database.
Separate tabs can use separate workspaces under the same OIDC login, without a mutable global workspace session.
Switching workspaces reloads the page so selections, drafts, private previews and live subscriptions do not carry into the new scope.

The control registry grants no media access. Camera usernames are unique on the shared ingress endpoint and resolve through opaque routing handles, followed by current Source credential/generation checks inside the selected tenant.
Camera credentials grant only Source ingress and cannot establish human membership.

## Credentials and encryption

Management identity is not source ingress authentication. Each logical Source has its own generated or custom credentials and a distinct virtual ingress root; several devices may share that Source.
Source credentials cannot become management or destination credentials. A source secret is stored only as a fixed-time verifier, so it cannot be recovered.
Destination/provider credentials need recovery for delivery and are encrypted in PostgreSQL with AES-GCM.

### Source credentials: verify, never recover

This sequence shows generated credentials. Custom passwords are supplied by the operator and omitted from the response.

```mermaid
sequenceDiagram
    participant Admin
    participant Server
    participant DB as PostgreSQL
    participant Edge as Camera edge
    Admin->>Server: Create or regenerate Source
    Server->>DB: Store HMAC verifier
    Server-->>Admin: Show secret once
    Edge->>Server: Private login callback
    Server->>DB: Read verifier
    Server->>Server: Fixed-time verification
    Server-->>Edge: Login result
```

### Destination credentials: encrypt, recover only for delivery

```mermaid
flowchart TD
    Admin[Destination secret] --> Encrypt[AES-GCM encryption]
    Key[Deployment key outside DB] --> Encrypt
    Encrypt --> DB[(Encrypted credential)]
    DB -->|delivery operation| Decrypt[AES-GCM decryption]
    Key --> Decrypt
    Decrypt --> Memory[Operation-scoped plaintext]
```

The encryption key is a persistent deployment secret outside the database. It must accompany a backup/restore; a missing or mismatched key fails closed for the dependent operation. Secrets are never placed in Valkey, telemetry, logs, errors, or routine fixtures.
APIs return a generated Source password only at creation or explicit credential replacement; custom passwords are never returned, and stored passwords cannot be read back.

## Constraints for changes

- Put the final authorization check in `ShutterSpool.Server`, before returning private bytes, previews, configuration, or live data.
- Treat the client, preview service, cache, and identity-provider roles as supporting layers, never as an independent authorization authority.
- Keep local grants and credential state durable in PostgreSQL; tolerate cache loss safely.
- Preserve issuer and subject as the stable person reference. Do not make provider-specific identities durable owners of media or workflow records.
- Keep source credentials isolated by source and virtual root; do not reuse them for management or delivery.
- Redact credentials, token material, and private media locations while retaining non-secret audit information for successful and failed attempts.

## Current limits

Shared-workspace isolation is limited to one Server and separate tenant databases/filesystem allocations.
Delegated/team roles, public links, Keycloak UMA/object resources, external key management and key rotation are not implemented by this boundary.
Hosted processing, storage enforcement and recovery still need qualification for the selected deployment.
An external OIDC provider still needs the documented client, claim and local-grant configuration; discovery alone does not prove compatibility.

For concrete current configuration, see the [Server security configuration](../../src/ShutterSpool.Server/README.md#local-security-configuration).
