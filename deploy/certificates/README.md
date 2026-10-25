# Supply and replace certificates

The operator supplies certificates, either by hand or through an external certificate service.
The package contains no ACME client, DNS credentials, renewal daemon or expiry watchdog.

## Required files

| File | Purpose |
| --- | --- |
| `https-fullchain.pem` | HTTPS leaf followed by intermediate certificates for the application/identity hostnames served by the proxy |
| `https-key.pem` | Matching unencrypted private key, readable only by its consumer/operator |
| `server.pem` | FTPS leaf/full chain and matching unencrypted private key |
| `root.pem` | Actual public CA root used by enabled FTPS clients; contains no private key |

Use the configured DNS hostnames in certificate subject alternative names and an appropriate server-authentication certificate.
Modern and trusted-LAN legacy FTPS share the configured ingest hostname; each listener retains its protocol policy.
Mount certificate directories read-only; give non-root consumers the required read permission without exposing keys to other users.
Server receives only the public FTPS root, for authorized client setup, never the FTPS private key.

The packaged edge validates presence, chain, hostname, key pairing, validity and server usage at startup.
Invalid input fails startup; it never enables plaintext fallback.
Operators monitor expiry and replace certificates before it. The package does not promise to stop a running edge automatically when a certificate expires.
Clients must retain normal certificate validation.

## Controlled replacement

1. Prepare the complete replacement in a separate private directory. Validate its chain, hostname, dates and matching private key with OpenSSL or the issuing service's standard tools.
2. Schedule a maintenance window. Block new management traffic and [drain each affected camera edge](../operations/README.md#stop-writers-before-capture) while Server stays available.
   A timeout aborts replacement: preserve the old generation and investigate; do not force-kill transfers.
3. Stop the proxy if replacing its HTTPS certificates. Confirm all consumers of changed files are stopped.
4. Replace the complete files in the mounted directory, preserving restricted ownership/modes. Retain the previous valid generation privately until checks pass.
5. Recreate the selected consumers using the installation's complete Compose file selection.
   Verify a new HTTPS session and a hostname/chain-validated FTPS connection through the declared control/passive route before reopening traffic.

If validation/startup fails, keep ingress closed and restore the previous still-valid generation before retrying.
Do not substitute an expired certificate, change the Source to plaintext, or disable trust checks.
Replacing a CA root may require updating camera trust; replacing a leaf under the same trusted root normally does not.
Synthetic fixture tests do not qualify public routing, real issuance, or a physical camera's trust store.

## Outbound trust

Reverse-proxy TLS termination does not remove Server's need to verify outbound OIDC/WebDAV HTTPS.
The chiseled-extra runtime already contains public CA certificates; it does not need curl or a package manager.
For an internal CA, mount an operator-maintained complete PEM trust bundle and configure the consuming runtime's trust path, preserving required public roots.
For Server, append an operator Compose override containing:

```yaml
services:
  server:
    environment:
      SSL_CERT_FILE: /run/trust/ca-bundle.pem
    volumes:
      - ./trust/ca-bundle.pem:/run/trust/ca-bundle.pem:ro
```

The public certificate bundle must be readable by UID 1654 and contain the necessary public roots plus the private CA, without private keys.
Include the override and trust file in backup custody; replacing the file requires a controlled Server restart.
Qualify that bundle against each used dependency; Keycloak/Java has its own trust configuration.
Use controlled restarts for startup-read credentials/trust settings. Never install a trust-all callback or disable certificate verification.
