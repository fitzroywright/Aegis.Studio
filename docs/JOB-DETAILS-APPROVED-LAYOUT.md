# Approved Job Details layout — 2026-10-10

Implements the approved navy/orange/brown Job Details mockup using the existing Today shell. The center holds job identity, a compact six-phase workflow ribbon, five information tabs, and the photography contact sheet. The right rail spans the whole page with Selected File, Needs Attention, and the preserved post-it Messages card. The left rail contains Navigation, My Workspaces, and permission-aware job Actions.

## Ownership and replacement

- `workspace-outlook.js` is the sole page renderer/controller for Job Details. It creates each panel in its final container and disposes clocks, listeners, and media requests on replacement.
- `job-details.js` owns the Job Details gallery, selected-file inspector, and compact workflow ribbon. It calls existing media/workflow APIs directly. It does not monkey-patch the Production gallery or relocate its toolbar.
- `job-details.css` replaces the previous Job Details stylesheet in full. Its component classes own their geometry, with no `!important` declarations. Shared shell styles still belong to `studio.css`.
- `studio-shell-nav.js` renders the actual job navigation/actions cards and footer counts through its existing renderer, rather than adding a second rail or overlay.
- Removed the dead Job Details toolbar-placement branch from `media-production.js` and unused toolbar/filter CSS from `studio.css`. Production retains its gallery implementation.
- Changed assets have an updated version in `index.html`. The direct job-opening path from PR #95 is retained.

## Real behavior

Media pages request 12 files using the existing server cursor API. Collection totals come from `collectionCounts`; page navigation uses `after`, `limit`, and `hasMore`. Search is explicitly labelled **Search this page** because the existing media API has no filename-search parameter. Grid/list controls affect the displayed files.

Selecting a file populates its preview, version, actual size, collection and subjects. Saving preserves production state, assignment, capture time, and multiple subject associations. Full preview, original download, version history, and preview regeneration use existing endpoints. Failed previews show a recovery message.

Selecting or dropping files starts a sequential upload queue immediately. Dropping onto a collection targets that collection. Subject photo navigation, subject uploads, and roster import remain functional. Failed uploads report their filenames and errors; automatic retries are avoided because ingest requests are not idempotent.

The workflow ribbon maps actual stages, including source/production corrections, cancelled/completed jobs, and explicit print/edit jobs without shoots. Needs Attention shows the actual next workflow action and blocked reason; no made-up deadlines or counts are displayed. Assignments & Workflow opens the existing complete permission-aware controls.

## Verification

- 20 Job Details interaction tests cover replies, subjects, panel navigation, permissions, direct opening, failures/retry, stale responses, cursor paging, collection filtering, file classification, search/list controls, uploads, workflow phases, and preview recovery.
- Existing workspace Job Details/reply regression passes.
- Full UI suite: 55/66 pass; the same 11 unrelated failures remain from the earlier baseline.
- Chromium viewport checks at 1920×1080, 1440×900, 1280×720, 1024×768, 800×600 and 390×844 show no JavaScript errors, horizontal document overflow, or page scrolling. Gallery, file inspector, messages and rails own their internal scrolling.
- Browser fixtures verify geometry and interaction, not live storage/thumbnail delivery. Safari, the live backend, and Kratos deployment still require local acceptance testing.

Recovery: revert this change to restore the PR #95 layout and direct job opener. No database migrations or backend contracts changed.
