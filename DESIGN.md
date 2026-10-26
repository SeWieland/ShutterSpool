---
name: ShutterSpool
description: A neutral dark workspace for Capture curation and delivery evidence.
colors:
  primary: "#527dff"
  focus: "#6d92ff"
  navy: "#03165c"
  page: "#111315"
  surface: "#1b1e22"
  preview: "#111315"
  border: "#3c424a"
  text: "#f0f3f7"
  muted: "#b4beca"
  favorite: "#f3a6a0"
typography:
  heading:
    fontFamily: "Manrope, ui-sans-serif, system-ui, sans-serif"
    fontSize: "clamp(28px, 4vw, 40px)"
    fontWeight: 650
    lineHeight: 1.15
    letterSpacing: "-.025em"
  body:
    fontFamily: "Manrope, ui-sans-serif, system-ui, sans-serif"
    fontSize: "16px"
    fontWeight: 400
    lineHeight: 1.5
rounded:
  control: "6px"
  panel: "10px"
spacing:
  small: "8px"
  control: "12px"
  compact: "16px"
  panel: "24px"
  section: "32px"
components:
  curation-panel:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.text}"
    rounded: "{rounded.panel}"
    padding: "24px"
---

# Design System: ShutterSpool

## Overview

Use a neutral dark photo workspace in Impeccable's Operate mode.
Sources, Curation, Gallery and Delivery form the implemented workspace. Curation autosaves shared Capture
metadata; Gallery supports viewing and explicit loaded-photo selection; Delivery exposes rules and per-Asset evidence.
Keep accepted ingest, preview availability, release and confirmed delivery separately readable.
The supplied full-color shutter artwork anchors the header and sign-in; reusable controls use Nuxt UI with native inputs where sufficient.
The initial slice is dark only, including on an operating system that prefers light.

## Colors

Use the frontmatter palette for the page, panels, preview surface, borders and text.
The semantic CSS tokens live in `main.css`; workspace styles consume them, and Nuxt UI's surface, text and border
variables map to the same tokens.
The logo supplies electric-blue primary actions and deep-navy supporting artwork. Links, focus and selection
use a lighter shade of the same blue (`#6d92ff`) to preserve small-text contrast on elevated dark surfaces.
Inline text links stay underlined without hover; the brand and photograph tiles retain their existing control styling.
Primary action text is dark for contrast; preserve neutral photographic surfaces rather than tinting every panel.
State also has a text label; color is never its only cue.
Notices use `#13264a` with `#d9e7ff` text and blue borders; errors use muted red with readable borders and explicit recovery actions.

## Typography

Use locally hosted Manrope with system fallbacks. Its OFL license accompanies the font; no third-party font service is used.
Capture names are the largest workspace heading; labels, variant names and delivery states support scanning.
Wrap long names in detail and expose a full accessible name when inbox titles truncate.

## Layout

Standard workspace width is at most 1440px, with 24px desktop gutters and 16px phone gutters; Full width expands the shell.
The header is 80px high on desktop and 68px at widths of 760px or less.
Settings keeps one bounded content column beside a compact Workspace/Storage rail; on phones the two labeled choices sit above it.
Form content remains bounded even with Full width selected.
The desktop detail preview sits beside a 384px inspector with a charcoal surface background; below 760px they stack.
The normal desktop Curation workspace fits the viewport, with all seven signed rating controls on one row.
Expanded evidence can scroll inside the inspector; phone layouts retain normal scrolling.
Gallery inspection uses the same remaining-viewport layout, keeping photo navigation visible on desktop.
Switching photographs retains the workspace shell and filmstrip; loading and read failures stay in the active inspector.
Gallery and Curation each use a desktop photo-size slider for 100–520px minimum tile width; the default is 230px with 12px gutters.
Phones use two columns, reduced to one below 350px, and compact caption padding. Minimum widths shrink to the available space at narrow widths or zoom.
Hide the photo-size slider on phones, where it does not change layout.
Grid previews fill a 4:3 frame by default; Aspect ratio offers 3:2, 4:3, 16:9 and 1:1.
Thumbnail cropping affects presentation only. Detail previews always contain the full, uncropped image in a stable black frame sized by the viewport, enlarging small derivatives to fit. Loading, navigation and refresh retain the frame geometry.
Preview boxes reserve space and show loading/failure feedback; arrival and interaction transitions use 180ms with reduced-motion support.
Controls need 44px touch targets and must remain usable at 320px without horizontal scrolling.
Sources and Delivery align their photo streams across rows. Connector/activity space absorbs different name widths;
long names wrap, and phones stack the identity, activity and photographs.

## Shapes

Use 10px radii for panels and previews, and 6px radii for inputs and notices.
Borders provide separation on the dark surfaces; avoid decorative depth.

## Components

Collapsible sections use native `details`/`summary` controls with a highlighted summary row at least 44px high.
Use one decorative chevron: right when closed, down when open. Keep the label and keyboard focus visible.
Raised neutral backgrounds distinguish summaries inside panels and nested disclosures from their surrounding surface.
Compact inline disclosures retain the same cue and touch target; toolbar disclosures preserve overlay alignment without adding an expansion gap.
Give each section boundary one divider owner so neighboring controls and content do not stack horizontal rules.
Static headings retain their section dividers and do not acquire collapse affordances.
Photograph inspectors stay open without a summary control on desktop; phones show the disclosure row.

Gallery tiles open photograph inspection with a read-only right panel; returning restores grid selection, scroll and focus.
Favorite sits at top left and selection at top right in Gallery and Curation grids. The icon-only heart uses a smaller visual shape inside a 44px circular semitransparent target, with a filled pastel-coral selected state. With Show details off it appears on hover or keyboard focus and stays available on touch.
Gallery Photos/Trash and the separate fullscreen action share the Filters/Appearance toolbar, aligned right. Fullscreen keeps direct Capture links and uncropped viewing.
Gallery fullscreen follows Experience mode: the photograph fills a pure black canvas while compact, neutral controls recede.
Its bottom transport pairs a labeled Play/Pause action with 3/5/10-second choices, keeping the selected interval readable without color.
Playback starts only on request and uses simple cuts after the photograph loads; loading and interruption feedback stays local to the viewer.
Keep Favorite independent of playback, preserve 44px targets at 320px, and keep keyboard-focused controls visible without moving focus on automatic advances.
Both fullscreen viewers hide controls after three idle seconds without animation; pointer movement, touch or keyboard interaction reveals them.
Keep actionable recovery feedback visible, and let a first touch wake hidden controls without activating an action.
Slideshow controls belong only to Gallery; Curation retains its editing controls and Trash has no playback.
Both fullscreen modes center Favorite at the bottom; Curation separates rating, Favorite and color-label groups with visible gaps.
Photograph navigation and fullscreen actions use familiar icons with accessible labels and 44px targets.
Photos dominate each tile; a compact name, label-colored rating and delivery symbol sit below.
Gallery ratings are centered in a 44px caption; the delivery action stays right with the same 14px icon size and
one shared caption background. Rule choices reuse these SVG ratings: filled 1–5, hollow 0 and crossed-out hollow -1.
Camera/tags appear on hover, with paired-Asset camera identities deduplicated and the same background/transparency
as the caption. Selection and details icons share round semitransparent hover/focus surfaces.
Per-Asset metadata and delivery attempts belong in detail without internal UUID clutter.
Use the full-color mark at 56px in the desktop header, 44px on phones (40px at 350px or less), and 72px on app sign-in.
Tiny contexts use the crisp six-blade SVG outline with a hexagonal aperture; blue, white and navy variants share the geometry.
The favicon uses a navy ground and blue outline. Preserve the full-color primary artwork.
Curation uses signed ratings -1/0/1–5, an optional fixed color label and individual Unicode text tags.
Rating filters show visible numbers beside the 1–5 star icons and use signed controls for exact clicks and inclusive
drag/Shift ranges, without duplicate From/Through selectors. Highlight every included value, including Not Rated when
the range crosses zero.
Serialized autosave preserves per-Capture local intent; commas remain part of each tag.
Release now stays disabled while committed edits are unresolved; Hold/Restore reflect saved policy before release.
Conflicts show server values beside retained draft values and require an explicit choice.
Released Captures are read-only in the workspace. The existing server policy still allows versioned current-curation edits.
Gallery delivery inspection groups destination names and variant/outcome rows, with blocked work and Retry now /
preview-and-confirm Reevaluate first. Delivered rows stay compact; paths, rules and attempts use Details.
Optional and secondary blockers remain visible. Metadata inspection stays read-only.
Curation shows saved Held/Waiting workflow symbols and a slim white remaining-delay bar even without captions.
Only authoritative timed waiting gets a bar; held/manual states do not. The final 20% breathes gently when motion
is enabled. Zero means release is due. Each view's Appearance offers independent persisted server-side Newest first / Oldest first ordering.

Use a flat, nearly black, texture-free background with a large six-blade SVG aperture centered at the bottom right.
Restrained lens rings and hairline blue construction curves give it a digital photographic character.
The aperture remains stationary at the slightly angled, closed Delivery position across all tabs.
Flat deep-charcoal blade faces, fine schematic seam lines and vector clipping preserve the cyclic overlap.
The fixed foreground housing covers the blade ends. The detailed native SVG rim retains the page typography,
engraved aperture marks, machined grooves, 180 grip ribs and slotted fasteners; its neutral body uses no radial metal gradient.
Background linework uses muted steel-blue #61718c, separate from the interface action blue #527dff. Only the rim's containing HTML layer rotates, by 1.5 degrees per tab,
with a soft six-second transition. No SVG geometry, clipping or filters animate. The background stays flat #080808; use only solid neutral charcoal fills and restrained steel-blue blueprint linework. No gradients or blur filters. Keep broad fills, engravings and construction lines dim; three small index crosses retain a subdued #7c91ba glint at 60% opacity. Preserve the machined collar, engraved scale and calibration detail as quiet photographic character.
Reduced motion keeps the ring at one fixed position across every tab. The decorative layer never takes input and hides in fullscreen.
The full-color shutter mark uses the supplied brand artwork.
Desktop navigation uses square underlines; phones use a persistent bottom navigation. Curation supports
independent comparison panes and an expanded grid. Fullscreen viewing uses uncropped derivatives on pure black.
Workspace preferences and independent Gallery/Curation appearance and filters persist for the signed-in account in this browser; editing drafts and selections stay in memory.
Filters and Appearance share one overlay position and are mutually exclusive; opening them does not reflow photographs.
Panels cap at 800px or available width. Filters use labeled rows in Favorite, Workflow, Source, Rating, Color label, Tag, Collection order;
choices wrap within each category. Workflow/Source multiselect icon buttons lead with All states/All sources, and
Clear icon/text occupies the final row.
Appearance groups photo size, aspect ratio and thumbnail fit before details and order, with a visible view-scoped reset and Workspace settings link below.
Workspace leads with its account/browser scope, then width and motion; Preview quality uses one named disclosure explaining the data cost.
Keep workspace reset secondary and separate from fields. Resets name and affect only their own scope.
Numeric preview errors stay beside their fields, and one compact contextual notice explains when browser changes cannot be saved.
Photo and bulk curation share one instant-save panel with direct Revert edits/Clear choices controls and busy feedback.
Photograph controls lead the inspector: rating, color, tags and a compact Favorite. The Collection control sits directly below tags,
with current membership and a search field; choices appear only when searching, in a bounded scrollable list.
Tag and Collection filter rows align their search inputs. Only Collection has a Manage action. Search uses sentence-case placeholders and updates as the user types.
File/delivery information and
storage actions use secondary disclosures; administrator erasure is nested within the storage entry point.
Recovery appears as a short contextual notice only for paused photographs. Settings → Storage leads with a capacity
ring and readable legend, followed by Storage limit and cleanup, Recovery and protection, and Preview storage.
Cleanup ages, history and privacy requests use named disclosures. Stack the composition on phones. Trash leads with Restore and its deadline.
Capacity segments are disjoint; overlapping protections, planned reclamation and historical removal stay in details.
Legend hover, focus and tap highlight a segment without hiding amounts. Saved capacity stays separate from proposed-limit comparisons;
inactive, over-limit, unavailable and dated last-known states never invent percentages or confirmed space.
Review changes replaces the editor with changed values, plain-language effects and authoritative impact estimates.
Back to editing retains the draft and restores position/focus; acknowledgements sit beside their consequences.
Privacy erasure uses a native dialog with a focused heading, exact photograph/variant scope, deliberate consent and a separate destructive action.
Keep external-copy limits and preservation holds readable. Return focus on cancellation; show per-surface progress with text and symbols,
while identifiers and raw diagnostics remain in Details. Use the same pattern in photograph details and Settings.
Write for hobby photographers: say photograph, explain what happens next, keep routine feedback short, and put
technical reasons in details. Destructive confirmations retain exact scope, copy-loss risk and recovery consequences.
Connected live state uses a compact accessible symbol; refresh icons appear when disconnected. Source/Delivery streams
retain aligned origins, with edge-only fading and reserved inactive activity dots.
The live symbol sits beside the brand text and breathes subtly only when connected, visible and motion is enabled.
Sources and Destinations share row geometry, thumbnail spacing, icon/name/corner-status composition and hover styling.
Recent destination work includes a Scheduled for Retry count when present. Destination selection keeps streams
visible and opens Rules/Work/Settings underneath; New destination uses an accessible plus aligned right in that row.
Describe bounded delivery summaries as recent work.
Logical Sources use a neutral network identity rather than a camera image; their type and selected connection profile stay visible.
Source settings use General, Connection and Security tabs with keyboard navigation and a persistent weaker-profile warning outside panels.
Connection presents declared endpoints and one-time credentials; Security presents profile availability and the configured public CA root.
Source history distinguishes unloaded, loading, empty and failed/last-known states with bounded Load/Retry actions.
Source authentication shows a dated historical outcome with separate coverage warnings; fresh pulses never imply camera presence.
Source and Delivery activity share a dashed connector and exactly three dots with the same staggered motion.
Source phases change connector color without changing its geometry. Three persistent counter rows show Receiving,
Preview pending and Received; pending previews count each Capture once, while receiving/received count transfers/originals.
The status icon on the Source opens native popover details with their top-left corner at the activation point,
adjusted to stay inside the viewport; keyboard activation anchors at the icon. Keep its hover hit area transparent
and retain a visible keyboard focus ring. Source selection uses the same blue icon border as destinations. Transient status text never enters the
photograph row or inserts a footer. Preserve one tile per Capture and stop motion for reduced motion or stale state.
Source switching confirms unsaved-setting abandonment. Separate destination forms preserve each other's drafts,
and delayed settings/rule outcomes cannot alter a newly reopened editor.

## Do's and Don'ts

The header Settings control opens Workspace; Storage settings in Trash opens Storage directly. Both sections share a return action to the originating workspace.
Retention controls live in Settings → Storage. Gallery keeps photo actions and a Photos/Trash
switch; the four phone navigation destinations stay intact. Favorite uses an independent heart, with removal
consequences available in contextual help; Favorite toggles never open a confirmation dialog. A single lifetime rail uses the server's real phase timestamps and freezes
when its observation is stale; expiry shows eligibility rather than claiming bytes are gone.

- Preserve focus, scroll and drafts during reconciliation.
- Browse authorized derivatives and clear private data/object URLs on authentication loss.
- Keep preview failures independent from curation and release.
- Do not imply that an empty delivery list confirms delivery or permits deletion.
- Do not change to light mode or introduce a second visual/component system in this slice.

## App sign-in

The app sign-in uses the softened workspace aperture behind an opaque charcoal panel, Manrope and the full-color mark.
Its rim uses the brighter Keycloak blue highlights and turns clockwise in a linear 900-second (15-minute) cycle; reduced motion disables it and hidden tabs pause it.
Gallery details keep all variant camera/file metadata visible in compact rows, with Capture-level state shown once.
Current curation is distinct from the collapsed At release snapshot; file sizes show binary units with exact bytes available through a keyboard/touch disclosure or hover title.

## Companion sign-in

The native Keycloak login theme retains the original richer #527dff aperture linework and full-color shutter mark.
The lens stays static at the closed Delivery position; an opaque charcoal form keeps credentials, errors and actions dominant.
Local Manrope, clear blue focus, inherited authentication controls and a bounded single-column composition preserve the workspace identity.
The working interface keeps its quieter steel-blue background. See the [theme guide](src/ShutterSpool.AppHost/Themes/README.md) for the tested Keycloak image and complete-artifact upgrades.
