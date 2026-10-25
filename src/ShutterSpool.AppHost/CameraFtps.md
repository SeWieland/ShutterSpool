# Development FTPS setup

This guide covers camera ingress through the local AppHost on Windows. Complete the [AppHost prerequisites](README.md#start-the-development-environment) first. Camera operators should use the [camera setup guide](../../docs/camera-compatibility.md#camera-setup).

The operator supplies certificates. ShutterSpool does not issue, renew, or reload them automatically. For packaged installations, see the [packaged certificate guide](../../deploy/certificates/README.md); the [camera guide](../../docs/camera-compatibility.md) records hardware observations and their limits.

## Supply and validate certificates

For explicit FTPS, configure `ShutterSpool:CameraIngress:CertificateDirectory` to a directory containing:

- `server.pem`: the leaf certificate, required intermediate certificates, and matching private key.
- `root.pem`: the public CA root that the camera should trust.

Keep private keys outside source control and do not place them in `root.pem`. The AppHost validates the certificate chain, hostname, validity period, server-authentication usage, and private-key match before enabling an FTPS profile. Certificate validation is not disabled for development. Synthetic certificates are only for automated tests.

Configure ingress explicitly, using the hostname present in the certificate and a LAN address reachable by the camera:

```powershell
aspire secret set ShutterSpool:CameraIngress:Profiles:ModernFtps:Enabled true --non-interactive
aspire secret set ShutterSpool:CameraIngress:ListenAddress YOUR_LAN_IPV4 --non-interactive
aspire secret set ShutterSpool:CameraIngress:AdvertisedAddress YOUR_LAN_IPV4 --non-interactive
aspire secret set ShutterSpool:CameraIngress:CertificateDirectory 'ABSOLUTE_PATH_TO_CERTIFICATE_DIRECTORY' --non-interactive
aspire secret set ShutterSpool:CameraIngress:Hostname YOUR_INGEST_HOSTNAME --non-interactive
aspire start --non-interactive
aspire wait server --timeout 60 --non-interactive
aspire wait camera-edge --timeout 60 --non-interactive
```

Allow only the camera LAN to reach TCP 2121 and 2122–2137 in the host firewall. `ListenAddress` controls host bindings; `AdvertisedAddress` controls passive replies and defaults to `ListenAddress`. Both are IPv4 addresses. The camera uses the configured DNS hostname for certificate validation. The edge requires TLS 1.2+ on control and data, with Source credentials and no client certificate. Private callbacks, upload isolation, and control/data peer checks remain enforced.

Enable profiles only when their certificates and ports are ready. Missing, invalid, or expired certificates fail startup closed. They never cause plaintext fallback or alter stored media or credentials. Check the certificate served to a client using a TLS-capable FTPS client that trusts `root.pem` and validates `YOUR_INGEST_HOSTNAME`.

## Replace a certificate

Arrange a maintenance window. Stop new camera uploads and wait for every active transfer to finish while Server remains available.
If transfers do not finish, leave the certificate files unchanged and investigate before proceeding.
Then stop the AppHost and confirm every enabled FTPS edge has stopped:

```powershell
aspire stop --non-interactive
```

Replace `server.pem` and `root.pem` as a complete pair, preserving private-key permissions, then start the AppHost again and wait for Server and each enabled FTPS edge:

```powershell
aspire start --non-interactive
aspire wait server --timeout 60 --non-interactive
aspire wait camera-edge --timeout 60 --non-interactive
aspire wait camera-edge-legacy --timeout 60 --non-interactive
```

Omit the wait for any disabled edge. Starting the AppHost reconstructs its graph and validates the supplied files before enabling FTPS; restarting only an edge resource does not rerun that validation.
Verify a new hostname/chain-validated FTPS connection before resuming uploads. Secret/configuration replacement takes effect after restart; no background renewal or reload runs.
If validation or startup fails, keep ingress stopped and restore the last known-good certificate pair before restarting the AppHost.

## Older cameras with legacy ciphers

Check the camera's latest firmware and retry standard FTPS before enabling compatibility. The Sony α7C needed compatibility on firmware 2.00, while firmware 2.03 passed standard FTPS.

For cameras that require older ciphers, enable the separate trusted-LAN compatibility edge explicitly:

```powershell
aspire secret set ShutterSpool:CameraIngress:Profiles:LegacyFtps:Enabled true --non-interactive
```

Allow TCP 2138 and 2139–2154 only from the camera LAN; keep modern ports available. The optional `ControlPort`, `PassiveStart`, and `PassiveEnd` keys under `Profiles:LegacyFtps` override these defaults; all enabled profiles must use disjoint ports. Compatibility can be enabled independently of the modern edge.

The Source must select **FTPS — compatibility (legacy)** in its Security tab, as described in [camera setup](../../docs/camera-compatibility.md#camera-setup). Valid credentials alone do not authorize this listener. Supply the camera operator with the same hostname and Source credential, and the separately configured compatibility control port, **2138** by default. The modern endpoint on 2121 retains its secure cipher defaults and accepts only Sources assigned to **FTPS — recommended**.

Compatibility adds only TLS 1.2 static-RSA AES-128/256-CBC/SHA ciphers to the separate edge. They lack forward secrecy: a later server-private-key compromise can expose recorded transfers. TLS below 1.2, plaintext control/data, and certificate bypass stay prohibited. This is a trusted-LAN exception, not public deployment qualification. Changing the Source choice invalidates existing camera sessions; reconnect afterward. Already admitted transfers can finish safely. Changing a Source to another profile preserves its secret and media and blocks further compatibility admission. Disabling the deployment profile preserves the saved selection but makes it unavailable for login or upload admission.

Each edge has finite connection/session limits and an `EdgeBufferBytes` tmpfs budget. Enabling the second edge adds bounded memory/connection cost; Server's central upload admission limit remains shared. Certificate replacement uses the controlled stop-and-start procedure above for both enabled edges.

## Connection failures before TLS

`500 Access denied: banned client IP` before the normal `220` greeting indicates the edge's temporary abuse-protection ban. Check the saved Source compatibility choice and credentials before retrying; credentials work only on the Source's selected profile. A generic connection failure does not establish a cipher mismatch or justify a downgrade. Wait for the ban to expire. Restarting an idle local-development edge through Aspire also clears its in-memory ban state, but disconnects sessions; do not interrupt active transfers. Keep Defender and certificate validation enabled.

## Runtime contract

Each profile accepts `ControlPort`, `PassiveStart`, and `PassiveEnd` under `ShutterSpool:CameraIngress:Profiles:<profile>`. Every enabled range must be disjoint. Each passive range has 2–64 ports, excluding its control port, within 1024–65535. Both FTPS profiles require encrypted control/data and normal server-certificate validation. Only the Source's selected profile admits its credentials; profile changes increment credential generation and require reconnecting without changing the secret. Removing a deployment capability preserves saved Source settings and media while blocking new login/admission.

Set `ShutterSpool:CameraIngress:Hostname` to the installation's ingest hostname. The UI receives the declared hostname and profile control port from Server; it never guesses a profile hostname or tests DNS per render/upload. `ListenAddress` controls the host LAN IPv4 binding (default loopback); `AdvertisedAddress` controls passive replies and defaults to `ListenAddress`. Restrict control/passive ports to the camera network; current compatibility qualification is trusted-LAN only.

The edge certificate directory is mounted read-only. Server receives only the configured public CA root path for the bounded, authorized CA download. The edge has no spool mount or exposed administration, SSH/SFTP, or WebDAV listener. Its private HTTPFs endpoint streams into ShutterSpool's spool-owned receive operation; a synchronous upload hook establishes successful completion before Asset publication and FTP success. Its account store is disposable; every login and upload admission checks PostgreSQL.

The AppHost generates and persists `Parameters:camera-callback-key` in User Secrets. Only Server and the edge receive it. The private callback endpoint uses a separate listener and shared-key authentication, and does not serve management, originals, or the web application. On Docker Desktop, authentication/upload hooks and returned HTTPFs URLs use `host.docker.internal` and the private proxy port, so recreated edges do not depend on a manual hostname alias; do not publish or forward this endpoint. Other deployments must supply an edge-reachable private callback base URL.

Only Server writes the spool. FTP virtual folders are empty protocol metadata under the spool's `.camera-directories`; originals use generated private spool keys. Neither the camera nor edge receives a host path or mount. Failed hook cleanup cannot delete authoritative originals or uncertain receive bytes. Configure `ShutterSpool:Spool:ReservedBytes` for payload admission and retain the spool with its PostgreSQL state across restarts. See the [transfer sequence](../ShutterSpool.Application/README.md#camera-transfer-flow).

`MaxConnections` defaults to 16, `MaxPerHostConnections` to 8, `IdleMinutes` to 5, and `HookTimeoutSeconds` to 60, without automatic HTTP retries. Authentication abuse protection is enabled. `EdgeBufferBytes` defaults to 1 GiB and bounds the edge's disposable `/tmp` pipe buffer with tmpfs. Source-specific transfer/size/bandwidth settings are described in the [Server guide](../ShutterSpool.Server/README.md). These are finite admission limits, not a burst-throughput claim; measure the actual host and camera before making a throughput claim.
