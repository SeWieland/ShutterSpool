# ShutterSpool icon set

The finished blue/cyan camera-shutter emblem is supplied on a full-bleed deep-blue square and on transparency.
The crisp minimal shutter outline is available in blue (`#527dff`), white and navy as native SVG.
All square store icons have sharp canvas corners; the stores apply their own masks.

| Platform                      | Use                                                                                                                                                                                         |
| ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| iOS App Store / asset catalog | `ios/AppIcon-1024.png` (1024 x 1024 RGB, opaque)                                                                                                                                            |
| Google Play listing           | `android/play-store-512.png` (512 x 512 sRGB RGBA, opaque pixels)                                                                                                                           |
| Android adaptive launcher     | `android/adaptive/` background, foreground, monochrome and XML template; put the files in the appropriate `res/drawable` and `res/mipmap-anydpi-v26` directories for Android resources |
| Android legacy launcher       | `android/legacy/mipmap-*/ic_launcher.png`                                                                                                                                                   |
| Windows desktop               | `desktop/ShutterSpool.ico` or `desktop/ShutterSpool-transparent.ico`, each containing 16, 24, 32, 48, 64, 128 and 256 px images                                                             |
| Web favicon                   | `web/favicon.svg`                                                                                                                               |
| PWA / browser                 | `web/pwa-192.png`, `web/pwa-512.png`, `web/apple-touch-icon-180.png`                                                                                                                        |
| Tiny UI, toolbar, notification | `minimal/outline-*.svg`; the outline is intended for small contexts                                                                                                         |

For prominent application-header and sign-in branding, use the supplied full-color artwork at a legible size
alongside the ShutterSpool name. The minimal outline is not the primary mark for those surfaces.

The Android adaptive foreground and monochrome artwork fit inside the centered 66/108 safe area.
The background is a plain deep blue, so Android can apply its device-specific mask.
The minimal SVGs use a clean six-blade shutter, central hexagonal aperture and uniform strokes on a 64px grid.
They replace the older traced outlines for small contexts; the approved full-color transparent mark retains its original gradients.
The web favicon uses the blue outline on the supplied deep navy, and the SPA copies it to `/favicon.svg`.
The obsolete cyan raster outline and contact sheet have been removed; the native SVGs are the current outline sources.

These are source assets. This repository has no native mobile or desktop application project; the platform variants are artwork resources, not app availability claims.
