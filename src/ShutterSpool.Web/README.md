# ShutterSpool.Web

Nuxt 4 / Vue 3 / TypeScript client-rendered curation UI using Nuxt UI and native controls; production static output is served by Server.

The dark, responsive SPA provides Sources, Curation, Gallery and Delivery, with OIDC PKCE sign-in,
versioned curation autosave, release controls and per-Asset delivery evidence.
For UI design, implementation and review, use [PRODUCT.md](../../PRODUCT.md) and the
[Impeccable UI workflow](../../docs/ui.md). The workspace uses tailored Nuxt UI/native controls, shared
dark-neutral tokens, the approved shutter artwork and locally served Manrope with its license.
Read the [core workflow](../../docs/core-workflow.md) for behavior and [system boundaries](../../docs/architecture/system-boundaries.md) for ownership.
The shared **About & licenses** control in the app header exposes ShutterSpool licensing and pinned SFTPGo attribution, with matching source, license and notice links generated from the camera-edge image metadata.

## Build and check

From a clean checkout, run `dotnet tool restore` in the repository root, then `npm ci`, `npm run typecheck`, `npm test`, and `npm run build` here to generate Server `wwwroot`.
The build, dev server and typecheck first run the source-root `tools/generate-attribution.mjs`. It reads the `camera-edge` digest pin from `deploy/images/compose.yaml`, resolves its linux/amd64 OCI labels, and writes source-root `artifacts/attribution/sftpgo.json` and the packaged `THIRD-PARTY-NOTICES.md` with matching source and terms links.
Image updates generate new version/source links automatically. Generation needs Docker Hub access and rejects missing or mismatched metadata; reviewing upstream terms remains part of dependency review.
The build checks generated bundle budgets before copying static output to Server.
The [bundle check](scripts/check-bundle.mjs) bounds initial JavaScript/CSS bytes and gzip sizes, limits the largest JavaScript chunk,
and verifies that the five workspace sections remain dynamic without static-HTML prefetches.
Use `npm run check:bundle` to recheck the current generated output and manifest; generate them first.
These are asset-size and loading-boundary checks, not measured load times.
The native Node checks cover serial/coalesced autosave, uncertain outcomes, revision conflicts, released-Capture
guards, reconciliation, comparison/drafts, zoom bounds, motion preferences and bounded preview requests using the existing TypeScript compiler.
Run these commands from this directory; there is intentionally no repository-root `package.json`.
Run `npm run generate-client` after building the Server OpenAPI document.
The command replaces the generated `app/api` directory; keep handwritten code outside it. Explicit media types preserve binary preview return types. The pinned Kiota bundle supplies the generated client's runtime. Set the request adapter's base URL to the selected Server origin. CI rejects generated-client drift, and repeated generation from the same document must produce identical files.
On Windows, stop the Aspire Web resource before `npm ci`: run `aspire resource web stop` from `src/ShutterSpool.AppHost`, run `npm ci` here, then run `aspire resource web start`. The Nuxt dev resource can keep a native module in `node_modules` open, causing npm's clean install to fail with an `EPERM`/file-in-use error; this is a file lock, not a dependency failure. Use `aspire stop` if the individual resource command is unavailable; do not delete `node_modules` or terminate processes to work around it.
If your npm policy defers esbuild's postinstall, run `npm install-scripts approve esbuild` and rerun the command; Nuxt cannot run until its local esbuild binary is installed.

## Workspace and state

Sources configures shared incoming upload connections; Curation handles photographic decisions; Gallery browses accepted Captures; Delivery explains outgoing work.
Settings opens Workspace for personal width, motion and advanced preview-quality preferences; Storage holds shared policy and capacity.
Gallery has a separate recoverable Trash view; its **Storage settings** action opens Storage directly.
The [retention guide](../../docs/retention.md) explains Favorites, recovery windows and paused work after Restore.
The [Collection guide](../../docs/collections.md) covers incoming defaults, independent photo filters, management and single/bulk moves within these workspaces.
The [Gallery guide](../../docs/gallery.md) explains fullscreen slideshow playback, intervals and interruption recovery.

Keep photographs and curation controls prominent; disclose operational evidence when it helps someone act.
[PRODUCT.md](../../PRODUCT.md) explains the intended experience, [DESIGN.md](../../DESIGN.md) the established visual system.

The SPA uses same-origin `/auth/config`, generated Kiota requests and `/live/changes` hints.
After sign-in, the shell loads the authenticated user's workspace memberships. In tenancy mode, choose a listed workspace before it mounts; a URL selection is confirmed against that membership list on every full page load. An incomplete membership query stays retryable and permits explicitly choosing a listed, confirmed workspace; it never opens an unconfirmed workspace or silently replaces a URL selection. The legacy single-workspace mode is used only when the Server explicitly reports that workspace selection is not required.
The selected workspace ID stays in the page URL as `?workspace=<guid>` and is sent as `X-ShutterSpool-Workspace` on scoped API and authenticated preview requests. The live hub receives the same ID in its URL because WebSocket handshakes cannot set the scope header. Server membership checks remain authoritative; a URL value never grants access.
The shell retains one selected workspace across `/` and `/captures/:id`; explicit Capture links open the requested photograph in Gallery. Switching workspaces confirms the existing unsaved-work departure guard, then reloads the shell so private drafts, previews and live state cannot cross workspace boundaries.
Paged Capture reads and a bounded tile window keep browsing independent of original media size.
Original downloads are not offered by this workspace; selection does not fetch originals.

### State ownership and lifetimes

`OperationalWorkspace` coordinates navigation and the active photograph.
The composables in [app/composables](app/composables) own Capture browsing, visit-scoped curation intent,
viewer interactions, grid measurement, account preferences and the shared foreground clock.
Keep these responsibilities with their owners when extending an interaction; Server remains the workflow authority.

Sources, Delivery, Trash and Storage settings load on demand; Sources and Delivery unmount when their view closes.
Storage settings stay mounted across Workspace/Storage changes during a Settings visit so their draft survives.
Delivery evidence reads begin when its disclosure opens.
Read owners cancel obsolete transports and reject replies from an older request, editor, photograph or authentication generation.
Cancellation of a read does not establish the outcome of a command.

### Browser preferences

Workspace preferences apply across views for the signed-in account in this browser.
Gallery and Curation each have independent Appearance controls for photo size, framing, details and server-side order.
Personal changes apply immediately; each reset affects only its named scope and leaves filters and workflow state alone.
The phone grid retains its fixed columns, so the photo-size slider appears only on desktop.
These settings change presentation only; shared storage policy remains separately authorized on Server.

Preferences use browser-origin `localStorage`, keyed by issuer and subject; there is no server profile or cross-device synchronization.
Loading validates allowlisted fields and copies legacy shared appearance values into both views without writing during hydration.
Explicit preference and filter changes queue a batched snapshot write; interaction end, account changes and scope disposal flush pending writes.
An account change flushes to the previous account's key before loading the next account. Unknown preference formats remain untouched.
Browser storage failures keep preferences usable in memory and show a compact notice; a successful later write clears a write-failure notice.
Tabs do not synchronize live: each keeps its in-memory presentation, and the last successful explicit change supplies the next reload's snapshot.
Background loading or migration must not overwrite another tab's newer saved record.

### Security and concurrency constraints

Bearer/refresh/ID tokens, private previews and curation drafts stay in memory.
Only short-lived OIDC/PKCE transaction state uses tab-scoped `sessionStorage` (five-minute expiry).
Validated workspace, appearance and filter preferences persist, along with account-scoped markers for unfinished destination setup.
Workspace selection remains in the active page URL and is not copied into browser storage. Capture selections and curation drafts do not persist. Credentials are write-only.
The Server owns authorization and workflow truth, even when the UI hides a control.

Curation autosave coalesces edits with at most one request in flight per Capture.
Opening values and unfinished tag input survive photo/layout changes during a visit; leaving the photo workspaces requires resolving or explicitly abandoning unsaved work.
Conflicts preserve intent for an explicit server/reapply choice.
Released curation is read-only in this frontend; Favorite remains a separate revision-checked action.
The shell also guards unsaved Source, destination and Storage edits when leaving their workspace.
Sign-out, routes outside the workspace and browser unload check unresolved work; browser unload uses the browser's own warning.

Kiota command transport retries are disabled.
An uncertain mutation response requires an authoritative reread before another action; it must not silently repeat a command.
Live subscription precedes snapshots; racing hints coalesce into a follow-up read.
Refresh scopes separate Capture, preview, Source, delivery, storage and erasure changes.
Inactive photo, delivery and storage views retain an invalidation for revalidation when opened; preview hints do not reload configuration.
Reconnect, explicit Refresh and 60-second foreground reconciliation recover missed hints while preserving drafts and the active photograph.
Stale observations remain dated last-known evidence rather than apparent live progress.

Source/Destination removal uses revision-checked delete-or-archive APIs and never removes original/provider files.
Sources shows each Camera / FTP upload Source and its single explicit profile: standard FTPS by default when enabled,
warned compatibility FTPS, or explicit unencrypted trusted-LAN FTP. General, Connection and Security tabs separate
policy, deployment-supplied endpoints/credentials and profile/CA setup. Several clients may share one Source.
Disabled profiles preserve saved settings; missing capability reads preserve drafts and block new assignments.
Deployment profiles and CA setup load once per Sources visit; reopen Sources or reload the page after deployment changes.
Live Source/transfer status and configuration conflict checks continue to reconcile independently.
FTPS setup downloads only the endpoint's configured public CA root and shows its identity and SHA-256 fingerprint.
Try existing client trust first, keep certificate validation enabled, and check applicable firmware before compatibility.
Profile changes invalidate existing logins and require every shared client to reconnect, while retaining the secret and media.
The forms submit explicit type/profile fields and retain revision-conflict and uncertain-outcome protections.
Source creation offers independent generated/custom usernames and generated/custom passwords, with effective deployment requirements before entry.
Connection settings replaces credentials with an explicit shared-device confirmation and copy actions for client setup values.
Custom password fields clear after submission; generated passwords appear only once. Both stay in memory and clear on navigation/access loss.
An uncertain write requires a configuration reread before another deliberate action; no automatic retry or secret recovery is offered.
Storage impact and privacy erasure use separate server previews and confirmations; incomplete impact cannot authorize automatic-removal activation.
Storage capacity uses disjoint authoritative categories and the latest checked saved quota state, independently of the editing baseline.
When the Server reports an assigned workspace allocation, Storage requires an active limit no greater than that allocation; the allocation is not deployment-wide free space.
The dedicated review binds changed values and estimates to an immutable draft and request generation; returning to editing retains the draft and restores position/focus.
Obsolete replies cannot replace a newer review, and incomplete movement/reclamation plans remain unknown.
Unsaved Storage drafts survive Workspace/Storage section changes; leaving Settings requires saving or explicitly abandoning them.
Personal preference changes never submit a Storage save.
Preview-cache configuration shows effective running values and takes effect after Server restart.
Privacy erasure uses a native confirmation dialog for the exact reviewed photograph and variants, then tracks the saved operation and per-surface progress.
Uncertain submission requires checking that operation rather than repeating the command. User/operator steps belong in the [retention guide](../../docs/retention.md#permanent-deletion-and-privacy-erasure).
Erasure hints and confirmed missing-Capture reads unmount affected private data and fence older reads; temporary outages retain unrelated drafts.

For exact keyboard behavior, inspect the controls and accessible labels beside their components and verify the affected interaction in a browser.

## Run and sign in

Provision persistent deployment secrets using the [AppHost README](../ShutterSpool.AppHost/README.md).
Build Web as above, then run `aspire start --non-interactive` and
`aspire wait server --timeout 60 --non-interactive` from `src/ShutterSpool.AppHost`.
Open [http://localhost:5089](http://localhost:5089), the Server-served SPA.
`/auth/config` provides the public issuer/client settings; the provisioned client is `shutterspool-web`.
Choose **Sign in** with the configured photographer account.
The exact local callbacks are `http://localhost:5089/auth/callback` and
`http://localhost:5089/auth/signout`; the browser origin is `http://localhost:5089` and PKCE S256 is required.

For a physical phone, use trusted HTTPS for both Server and the issuer, with certificates valid for their exact hosts.
Configure Kestrel's URL/certificate outside Git and set Server authority/bootstrap issuer to the externally advertised issuer;
preserve the subject, audience, grants and persistent signing/encryption keys.
Provision the public client's redirect and post-logout allowlists with exactly `<Server origin>/auth/callback`
and `<Server origin>/auth/signout`, its browser origin with that same origin, and PKCE S256.
A separate browser origin also requires its exact HTTPS origin in `ShutterSpool:Security:AllowedOrigins`.
Do not use wildcard redirects or disable TLS validation. A reverse proxy must preserve WebSocket upgrades,
use explicitly trusted forwarding and suppress query logging for `/live/changes` before use.
Keep tokens in Authorization headers for API/media; the live hub is the sole query-token exception.
Synthetic browser/viewport checks do not establish physical-camera or phone compatibility.
