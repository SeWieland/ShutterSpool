# ShutterSpool.AppHost

Aspire resource graph: Server, PostgreSQL, Valkey, Imaginary, identity and selected protocol edges, plus Nuxt for development.

AppHost keeps the local resource graph and its private connections together so developers can run the product as a whole.
Development runs use local settings. Publish mode generates the Compose candidate described in the [operator package guide](../../deploy/README.md).
Generating a package does not establish release or deployment compatibility; inspect its manifest and declared platform limits.
For the design behind these resources, read [system boundaries](../../docs/architecture/system-boundaries.md).

## Prepare a Compose candidate

The pinned `Aspire.Hosting.Docker` integration generates the core service graph; `ComposePackage` supplies file secrets, persistent volumes and private callbacks.
Small additive Compose components provide bundled dependencies and the selected proxy without duplicating the application graph.
Publish mode omits Nuxt development and the Dashboard; the Server image contains generated static assets.
The [candidate builder](../../tools/build-release.py) supplies explicit version/commit inputs, pins upstream images and retains OCI SBOM/provenance without publication.
It generates modern-FTPS and all-camera-edge cores; operators select independent PostgreSQL, Valkey, Keycloak and Traefik/nginx/existing-proxy components.
AppHost restore selects `packages.win-x64.lock.json` or `packages.linux-x64.lock.json` on the supported build hosts; candidate builds preserve tracked sources and locked production dependencies.
Publish mode does not require local ingest certificates or export local parameter secrets.

From the repository root, select an exact commit and shared candidate version:

Install Aspire CLI 13.6.0, matching the candidate workflow. Pass `--aspire <executable-path>` when it is not the default CLI; the builder rejects another version.

```sh
python3 tools/build-release.py --version 0.1.0-alpha.1 --revision <exact-commit-sha> --output <new-output-directory>
```

The builder rejects source/version mismatch and unhydrated LFS pointers, retains a Linux amd64 OCI image with SPDX SBOM/SLSA provenance, and hashes package contents.
It includes artifact-local operator instructions; operators need neither Aspire nor the source tree.
Use `--allow-dirty` only for an explicitly labelled development build; it cannot establish a commit-qualified release.
The [private candidate workflow](../../.github/workflows/candidate.yml) runs manually from trusted `main` with an explicit prerelease version and exact source commit on `main`; pull requests and pushes do not create candidate bundles.
Once the access configuration has been verified and `GHCR_CANDIDATE_STAGING_ENABLED=true` is set, its staging job uses `packages: write` to retain the image and Compose bundle in private GHCR.
The local builder above performs no registry writes. Public promotion is a separate protected-environment approval step; neither a build nor private staging qualifies a release.
See [package checks](../../tests/README.md#public-python-and-shell-checks) for synthetic verification and its limits.
Follow the [release policy](../../docs/release-policy.md) for exact-artifact approval, one reviewed notes source and separately authorized publication.

## Start the development environment

Before the first run, set a local database password with `aspire secret set Parameters:postgres-password <password>`; it is stored in AppHost User Secrets, never in the repository. The PostgreSQL volume and that password must be retained or reset together.
Set `Parameters:keycloak-administrator`, `Parameters:keycloak-administrator-password`, `Parameters:shutterspool-administrator`, `Parameters:shutterspool-administrator-password`, `Parameters:shutterspool-administrator-subject`, and `Parameters:security-encryption-key` as AppHost User Secrets before starting the default local Keycloak companion. The subject must be a UUID and is used for both the Keycloak account and Server bootstrap grant; the encryption key must be a base64-encoded 32-byte persistent deployment secret. Keycloak stores its state in PostgreSQL; the reconciliation job applies the realm, clients, role, and initial administrator without requiring its console.
The companion mounts the native [ShutterSpool login theme](Themes/README.md) read-only; realm reconciliation validates its local resources before selecting it. Authentication forms and messages remain inherited from Keycloak. The theme guide records compatibility, complete-artifact upgrades and the bundled fallback configuration.

Fresh administrator provisioning preserves the configured subject. An existing exact username must have that same Keycloak ID; otherwise reconciliation fails before changing its password or role. Recovery updates the explicit subject parameter to the verified existing account ID and recreates the AppHost so the Server receives the new bootstrap configuration. Keep the existing account, Keycloak database and signing/encryption keys.
Run `aspire start --non-interactive` in this directory, then `aspire wait server`.
The local browser client permits the exact `/auth/callback` and `/auth/signout` redirects at `http://localhost:5089`,
matching Server's launch profile, and requires PKCE S256. If that origin changes, update
`Realms/shutterspool-web-client.json` deliberately; do not use wildcard redirects.
See the [Web README](../ShutterSpool.Web/README.md#run-and-sign-in) for browser sign-in and trusted-phone HTTPS.
`aspire wait --timeout` takes whole seconds, for example `aspire wait server --timeout 60`.
Use `aspire resource server stop` before a direct `dotnet build` on Windows; the running process locks its executable.
After a Web build, use `aspire resource server rebuild` before checking Server static files.
Imaginary has no spool mount or remote URL source; the Server sends selected JPEG bytes over its private endpoint. Its disposable cache is under `.data/previews` in the local AppHost configuration.
Preview tuning parameters are `Parameters:preview-max-concurrent-generations` (default 32), `Parameters:preview-queue-timeout-seconds` (60), `Parameters:preview-generation-timeout-seconds` (90), and `Parameters:preview-prewarm-mode` (`Off`, `GridOnly` by default, or `Full`). They bound each Server replica's concurrent source streams, queue wait and generation time; prewarming uses one background worker per replica. Imaginary uses separate `Parameters:imaginary-concurrency` (20 requests per second), `Parameters:imaginary-cpus` (2, also enforced as a local container CPU quota) and `Parameters:imaginary-http-timeout-seconds` (120 for both read and write). Set these through AppHost configuration or User Secrets for the host's capacity; increasing the Server limit does not raise Imaginary's CPU allocation. Kubernetes deployments must set their own CPU limit.

## Separate application data for the initial migration baseline

An incompatible older installation requires a fresh application database and empty spool at separately selected locations.
Set `ShutterSpool:DatabaseName` to a new database name and `ShutterSpool:Spool:RootPath` to a new empty absolute directory
before `aspire start --non-interactive`. Environment equivalents are `ShutterSpool__DatabaseName` and `ShutterSpool__Spool__RootPath`.
Incompatible existing databases fail startup before schema/data mutation. Recreate Sources explicitly and update every sharing device's credentials/settings.

Keep the original database and spool together and untouched, with their original encryption/signing keys and configuration.
Retain the shared PostgreSQL volume and Keycloak state. Startup never deletes or resets them.
Rollback runs the previous application against that original pair; do not open the new baseline with the old application.
There is no supported downgrade conversion or backward transfer of newly accepted media.

## Camera ingress

Declare each needed profile through `ShutterSpool:CameraIngress:Profiles:<profile>:Enabled=true`; all default to disabled.
Disabled profiles start no edge. An enabled edge serves many logical Sources and may start before any Source exists.
Creating or editing a Source never changes this resource graph.

| Profile | Edge resource | Control port | Passive range | Security |
| --- | --- | --- | --- | --- |
| `ModernFtps` | `camera-edge` | 2121 | 2122–2137 | Recommended explicit FTPS, TLS 1.2+ |
| `LegacyFtps` | `camera-edge-legacy` | 2138 | 2139–2154 | Qualified compatibility FTPS cipher exception |
| `PlainFtpLan` | `camera-edge-plain` | 2155 | 2156–2171 | Unencrypted; trusted local networks only |

Each profile accepts `ControlPort`, `PassiveStart` and `PassiveEnd` under `ShutterSpool:CameraIngress:Profiles:<profile>`. Every enabled range must be disjoint.
Each passive range has 2–64 ports, excluding its control port, within 1024–65535.
Both FTPS profiles require encrypted control/data and normal server-certificate validation.
Only the Source's selected profile admits its credentials; profile changes increment credential generation and require reconnecting without changing the secret.
Removing a deployment capability preserves saved Source settings and media while blocking new login/admission.

Set the common `ShutterSpool:CameraIngress:Hostname` to the installation's ingest hostname (development default `ingest.shutterspool.com`).
The UI receives the declared hostname and profile control port from Server; it never guesses a profile hostname or tests DNS per render/upload.
`ListenAddress` controls the host LAN IPv4 binding (default loopback); `AdvertisedAddress` controls passive replies and defaults to ListenAddress.
Restrict control/passive ports to the camera network; current compatibility qualification is trusted-LAN only.

For either FTPS profile, `CertificateDirectory` must contain `server.pem` (full chain and matching private key) and `root.pem` (the actual selected public CA root).
The edge mounts this directory read-only. Server receives only the root's configured path for the bounded, authorized CA download;
never put private keys in `root.pem`. Invalid certificates fail edge startup closed.
Follow [development FTPS setup](CameraFtps.md) for supplied certificate validation and controlled replacement, and [camera setup](../../docs/camera-compatibility.md#camera-setup) for client instructions.
The [camera guide](../../docs/camera-compatibility.md) distinguishes actual hardware observations from synthetic checks.

When changing the graph, stop/restart through Aspire, then `aspire wait server --timeout 60 --non-interactive`
and wait for each enabled edge. Source edits require no graph restart.
Build before setting ingress environment variables, then use `aspire start --no-build --non-interactive` so build-time OpenAPI discovery does not inherit incomplete callback settings.

The edge uses the Community SFTPGo image pinned in [Program.cs](Program.cs). The [third-party notices](../../THIRD-PARTY-NOTICES.md#sftpgo-camera-ingress) identify its matching source and license terms. It has no spool mount or exposed administration, SSH/SFTP or WebDAV listener. Its private HTTPFs endpoint streams into ShutterSpool's spool-owned receive operation; a synchronous upload hook establishes successful completion before Asset publication and FTP success. Its account store is disposable; every login and upload admission checks PostgreSQL.

Map and allow exactly the configured profile TCP ports in the host firewall; passive host and container port numbers match and are not proxied.
Control/data peer checks stay enabled; active FTP and SITE are disabled.

The AppHost generates and persists `Parameters:camera-callback-key` in User Secrets. Only Server and the edge receive it. The private callback endpoint uses a separate listener and shared-key authentication, and does not serve management, originals or the web application. On Docker Desktop, authentication/upload hooks and returned HTTPFs URLs use `host.docker.internal` and the private proxy port, so recreated edges do not depend on a manual hostname alias; do not publish or forward this endpoint. Other deployments must supply an edge-reachable private callback base URL.

Only Server writes the spool. FTP virtual folders are empty protocol metadata under the spool's `.camera-directories`; originals use generated private spool keys. Neither the camera nor edge receives a host path or mount. Failed hook cleanup cannot delete authoritative originals or uncertain receive bytes.

The AppHost forwards `ShutterSpool:Spool:ReservedBytes` to Server, defaulting to 1 GiB. Configure that reserve for payload admission and retain the spool with its PostgreSQL state across restarts. The [transfer sequence](../ShutterSpool.Application/README.md#camera-transfer-flow) shows the acknowledgement boundary.

`MaxConnections` defaults to 16, `MaxPerHostConnections` to 8, `IdleMinutes` to 5, and `HookTimeoutSeconds` to 60, without automatic HTTP retries. Authentication abuse protection is enabled. `EdgeBufferBytes` defaults to 1 GiB and bounds the edge's disposable `/tmp` pipe buffer with tmpfs. All are configurable under `ShutterSpool:CameraIngress`; invalid limits fail startup. Source-specific transfer/size/bandwidth settings are described in the [Server guide](../ShutterSpool.Server/README.md). These are finite admission limits, not a burst-throughput claim; measure the actual host and camera before making a throughput claim.

Create a Source through authorized management using generated credentials or a custom username/password.
Set `ShutterSpool:CameraCredentials:MinimumPasswordLength` through AppHost deployment configuration to change the default minimum of 12
(valid 1–128; maximum password length 128). Values below 12 deliberately permit weaker protection.
The [Server credential reference](../ShutterSpool.Server/README.md#camera-credentials) describes exact input/generation rules and API compatibility.
Existing credentials remain usable after policy changes. Replacement affects every device sharing the Source, while preserving its media.
Enter the credentials in the camera profile; generated passwords are shown only once and custom passwords cannot be read back.
The virtual root is initialized on successful login, so no manual `MKD /` or seed upload is needed.
Use the hostname, control port and security settings in [camera setup](../../docs/camera-compatibility.md#camera-setup), with a destination directory within `/`.
Downloads, deletion, arbitrary rename, links, append and resume are excluded.
Distinguish physical-camera observations from synthetic checks, and keep private filenames and media out of logs.
