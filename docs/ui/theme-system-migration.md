# Studio CSS ownership and theme contract (migration in progress)

## Canonical files

- `wwwroot/css/core/studio-legacy.css`: unchanged baseline CSS until selectors are extracted into named components. Do not add new rules here.
- `wwwroot/css/core/tokens.css`: shared semantic design tokens (currently a placeholder; must be populated as extraction proceeds).
- `wwwroot/css/components/*.css`: reusable components, shared card/row/dialog/navigation behavior; no theme-specific color literals in new rules.
- `wwwroot/css/pages/*.css`: page layout, density, scrolling, and positioning. New appearance colors must be semantic variables.
- `wwwroot/css/components/theme-surfaces.css`: shared selectors consuming semantic theme tokens.
- `wwwroot/css/themes/standard.css`: canonical existing Studio styling. Standard follows the OS preference via `theme.js`.
- `wwwroot/css/themes/light.css`: pale blue and white palette inspired by the supplied reference.
- `wwwroot/css/themes/dark.css`: navy/charcoal palette.

## Permanent policy

1. Preserve Today shell structure, compact density, logo, no-sidebar rule, and page-only internal scrolling.
2. Never append a duplicate selector/property to fix a component. Edit its owning stylesheet.
3. Components use `var(--theme-*)` or component-specific semantic tokens for colors, surfaces, borders, and shadows. Themes define those values.
4. All three themes must be checked for every component modification, including modals, hover/focus/disabled states, grids, and empty states.
5. No independent CSS layout forks per theme; theme files should supply appearance values, not geometry.
6. Remove old root CSS files and unused `themes.css` / `variants.css` only after verifying no dynamic references and passing browser checks.
7. Keep stylesheet ordering stable during migration; move rules only with visual regression evidence.

## Migration status

The root CSS files have been copied into `css/components`, `css/pages`, and `css/core`, and `index.html` loads the new paths in the previous order. This is a staged relocation, **not yet a complete selector-level consolidation**. Legacy files remain on disk for compatibility with scripts/tests that may still reference them. The very large `studio-legacy.css` must be decomposed by selector ownership, duplicates removed, and hard-coded colors migrated to tokens before the draft PR is ready to merge.

## Validation required before merging

Run CSS ownership tests and the full UI test suite; use Chromium screenshots for Today, Jobs, Job Details, Front Desk, Workspaces, Media, Messages, Pipeline, Profile and modal dialogs at desktop and short viewports in all three themes. Check browser back/forward, theme persistence, Standard OS-following behavior, and no console errors. Do not merge based solely on static file relocation.
