# ShutterSpool product context

<!-- impeccable:product-schema 1 -->

ShutterSpool is a self-hosted camera ingest, transient curation and reliable delivery product.
This brief records the implemented product's purpose and durable design constraints.
Use the [user and operator guides](docs/README.md) for tasks, [architecture](docs/architecture/system-boundaries.md) for boundaries, and [UI guidance](docs/ui.md) with [DESIGN.md](DESIGN.md) for interface changes.

## Platform

web

The responsive Nuxt 4 / Vue 3 / TypeScript web workspace is a static SPA served by the .NET Server.
The runtime includes camera FTP/FTPS ingress and local/WebDAV delivery.
A native mobile client, phone-library import, video playback and shared invitation galleries are not available in this repository.

## Users

Photographers running their own camera-to-storage setup are the primary audience.
They need reliable copies without vendor-app dependence, quick curation on a phone, and setup or delivery inspection on a desktop.
One person may operate the installation and curate photographs, but these are distinct tasks with different information needs.

## Product Purpose

Receive camera media, keep accepted originals safe, optionally curate each Capture, then deliver the right Assets to configured destinations.
Success means a photographer can distinguish what arrived, what awaits curation or release, what each destination confirmed, and what needs attention after interruption.

## Positioning

ShutterSpool complements destination storage and photo-management tools.
Its spool is temporary, quota-bounded custody rather than a permanent archive or editor.
Favorites can retain fully delivered originals while capacity permits; the heart is never a backup guarantee.
Trash provides a configured recovery interval while continuing to consume storage.

## Operating Context

The workflow is source → durable spool → Capture grouping → optional curation → release → routing → delivery.
Expect bursts, interrupted transfers, restarts, slow previews and destination outages.
Keep the photographic decision prominent and disclose file, storage and delivery detail when it helps someone act.
Sources configure incoming connections; Curation makes decisions; Gallery browses photographs and Trash; Delivery explains outgoing work.
Settings separates personal Workspace preferences from shared Storage policy.

## Capabilities and Constraints

- An Asset is one independently identified immutable original. A Capture conservatively groups variants, such as RAW and JPEG, with shared rating, color and tags. Uncertain pairing keeps Assets separate.
- Accepted ingest, pending curation, release and confirmed delivery are distinct outcomes. A preview does not prove durability, and camera acknowledgement does not mean destination success.
- PostgreSQL owns workflow truth; the spool owns bytes. Valkey, previews and telemetry are disposable. Reconcile against authorized state after reconnects and uncertain mutations.
- Source curation can be Immediate, Delay or Manual. Hold pauses release. Reject alone does not delete media. Release freezes curation and routing context.
- A Collection gives each Capture one named organizational home. Moves and renames do not move original bytes, rewrite delivery history, repeat delivery or extend retention.
- Delivery is at least once, with fixed recorded targets, explicit conflict handling and distinct Retry, Resend and Reevaluate actions.
- Retention requires policy, protection checks and exact removal authority. An empty delivery set, stale capacity observation or expired worker lease cannot authorize deletion.
- Lists, media, mutations and live status are server-authorized. Camera credentials grant upload access only; generated passwords appear once and custom passwords are never returned.
- Previews are authorized, disposable derivatives. Grid browsing never fetches originals or RAW files, and a preview failure cannot block release or delivery.

The [retention guide](docs/retention.md) owns user-visible lifetimes, Favorites, Trash, removal and privacy-erasure limits.
The [camera guide](docs/camera-compatibility.md) distinguishes physical-camera observations from manufacturer documentation and synthetic checks.
The [maintenance guide](deploy/operations/README.md) explains matching database/spool/key custody and the supported restore boundary.

## Brand Commitments

Use the ShutterSpool name and supplied full-color [shutter artwork](assets/icons/README.md) prominently in the header and sign-in.
Reserve the minimal outline for small contexts such as favicons.
Keep a restrained neutral-dark photographic workspace with precise typography, quiet blue accents and locally served Manrope with its license.
Photographs dominate; controls retain familiar accessible behavior beneath tailored proportions and styling.

The aperture background has fixed blades and a slowly rotating outer rim between main views.
It never delays input, remains static with reduced motion, and disappears in fullscreen inspection.
Fullscreen fits the uncropped photograph against black and provides keyboard/touch access to controls.
Use warm, plain language with concrete recovery actions; preserve deletion, security and compatibility consequences when shortening copy.

## Product Principles

1. Earn trust through accurate state and recoverable outcomes.
2. Keep accepted originals safe until explicit policy permits removal.
3. Keep photographers in control of curation, release and recovery.
4. Make thumbnail browsing efficient on phones and desktops while keeping detail available.
5. Keep media and credentials private by default; describe actual guarantees precisely.
6. Avoid duplicate controls, invented countdowns and unsupported compatibility or performance claims.

## Accessibility & Inclusion

Use semantic controls, visible keyboard focus, accessible names and errors, sufficient contrast and touch-friendly targets.
Convey status through text or symbols as well as color; respect zoom and reduced motion.
Keep actions understandable during loading, empty, failed, offline and partially completed workflows.
Use synthetic media for development and review; do not fabricate testimonials, customer evidence or licensing claims.
