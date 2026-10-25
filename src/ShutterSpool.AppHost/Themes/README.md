# Keycloak login theme

Native login-only theme for the supported companion. It extends `keycloak.v2` without copying forms or messages.
The inherited server-side authentication flow remains owned by Keycloak.

The theme was verified with the repository's `26.6` image resolving to Keycloak **26.6.4**:
`quay.io/keycloak/keycloak@sha256:0aae0de7fca85525f727d3354df17896092de8bb26ae4c12d89c77e5df8cbce4`.
This is compatibility evidence, not a replacement for release-manifest pins or supported-platform qualification.

Artwork uses the repository's Git LFS storage. Hydrate source assets with `git lfs pull` before installation;
preflight rejects unhydrated pointer files. Release packaging must include the actual binary assets.

Mount the entire `shutterspool` directory read-only at `/opt/keycloak/themes/shutterspool` before realm selection.
AppHost mounts the same resources in the companion and reconciler. `select-login-theme.sh --check` verifies required files;
`select-login-theme.sh` uses an authenticated `kcadm` session to update only `shutterspool` realm's `loginTheme`.
Do not expose the administrative session or bootstrap credentials in logs or documentation.

The artwork is the original blue aperture geometry, held at the closed Delivery position. The full-color logo,
Manrope font and font license are supplied locally. There are no template overrides, new scripts, blur filters,
idle animations, third-party asset requests or new authentication methods.
The shared realm display name supplies accessible brand text even if the decorative logo fails to load.

## Upgrade and fallback

Keep production theme/template caching enabled. Install a complete versioned artifact before selecting it;
do not update live files one at a time. Restart/roll out the companion through the supported packaging lifecycle.
Version changed resource filenames and their CSS references on future releases to avoid stale browser assets.
Check the exact parent classes/templates and enabled required actions again whenever the supported Keycloak image changes.
Do not assume a readable resource directory proves template or image compatibility.

For a known-good previous artifact, restore that artifact and reconcile its theme selection.
For the bundled fallback on an already running companion, authenticate `kcadm` using the supported operator
secret-handling mechanism, then run `/bin/sh /opt/shutterspool/select-login-theme.sh` in that companion with
`SHUTTERSPOOL_LOGIN_THEME=keycloak.v2`. This direct path updates only `loginTheme`, without merging a realm snapshot,
rerunning account bootstrap, resetting credentials or replacing issuer state. It requires no Keycloak-console visit.

Persist `ShutterSpool:Keycloak:LoginTheme=keycloak.v2` in AppHost configuration
(environment `ShutterSpool__Keycloak__LoginTheme=keycloak.v2`) so subsequent reconciliation keeps the fallback.
An AppHost restart runs the existing full bootstrap, which applies the configured initial administrator password;
it is not a credential-preserving theme-only operation. Use direct selection when that reset is unwanted.
The setting is passed to the selection script as `SHUTTERSPOOL_LOGIN_THEME`.
Set it back to `shutterspool` after installing and checking a complete compatible artifact, then select it directly.

The [package guide](../../../deploy/README.md) covers installation and image pins; the [maintenance guide](../../../deploy/operations/README.md) covers upgrades and rollback.
