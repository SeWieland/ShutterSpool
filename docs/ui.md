# Designing ShutterSpool interfaces

The photographic workspace is transient, responsive and usable by keyboard and touch.
Read [PRODUCT.md](../PRODUCT.md), [DESIGN.md](../DESIGN.md), the request and relevant components before changing it.
The running interface, shared tokens and supplied [shutter artwork](../assets/icons/README.md) establish its visual identity.

## Work within the implemented design

Use the installed Impeccable method when available, with **Operate** guidance for the workspace and **Read** guidance for documentation.
Run its context loader once per session with the relevant source or route as target, from the project directory.
Resolve the launcher from the installed skill rather than recording a machine-specific path here.
If loading fails, disclose that limit and inspect the existing product/design context and source directly.
Do not invent missing context or silently repair unrelated design metadata drift.

The current stack is Nuxt 4 / Vue 3 / TypeScript with tailored Nuxt UI and native controls.
Reuse its dark-neutral tokens, Manrope typography, proportions and accessible component behavior.
The [build preference](../.impeccable/config.json) is design-first; a small refinement within the established design does not require a new mockup set.
Ask about material product or visual-direction changes, while resolving routine implementation details within the request.
Keep reusable design decisions in DESIGN.md and its sidecar; do not put internal design narration in delivered markup or bundles.

## Keep the photograph and workflow understandable

Curation is optional per Source. It opens by default when any Source uses Delay/Manual; otherwise Gallery opens.
Hide Curation when all Sources use Immediate and no older Capture still needs review or is held.
Keep photographs and curation actions prominent; disclose file, delivery and storage details when useful.
Separate accepted ingest, pending curation, release and confirmed delivery.
Never imply that a preview proves durability, a camera acknowledgement proves delivery, or an empty delivery list permits deletion.

Use clear stars, tags, color and independent Favorite controls with RAW/JPEG variant identity available.
Show real release/retention intervals from server facts rather than invented countdowns or stale animated progress.
Settings separates personal Workspace preferences from shared Storage policy; Gallery provides recovery access without becoming an administration screen.
Deletion and privacy erasure retain their distinct server previews, authority and confirmation requirements in the [retention guide](retention.md).

## Required qualities

- Thumbnail-first browsing; grids do not fetch originals or RAW files.
- Structural responsiveness at narrow widths and zoom, with stable photo frames and usable bulk actions.
- Semantic controls, visible focus, accessible names/errors, sufficient contrast and touch-friendly targets.
- Status conveyed through text or symbols as well as color.
- Clear hover, focus, active, selected, disabled, loading, empty, error and recovery states.
- Motion that clarifies state without delaying input; honor reduced motion and keep fullscreen inspection calm.
- Preservation of focus, scroll and drafts during authoritative reconciliation.
- Explicit rereads after uncertain mutations, without silently repeating commands or discarding edits.
- Write-only secrets and useful redacted configuration errors.

## Verify visible changes

Use synthetic or explicitly authorized media, without private location metadata or credentials.
Inspect the running surface at representative desktop and phone widths, including keyboard paths and relevant loading/failure states.
Check console failures and retain concise state/viewport evidence.
Build first, inspect relevant states together, fix concrete findings in one batch and confirm the affected states.
Screenshots alone do not prove behavior, and component checks alone do not prove usability.
If browser access is restricted, record the actual failed operation and leave that evidence unclaimed; preserve host authentication and trust settings.
Substantive visible changes need independent behavior, code-quality and UI review.
