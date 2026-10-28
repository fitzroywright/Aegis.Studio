# Studio presentation ownership

Change the current component or owning rule directly. Do not append a replacement renderer, reassign an existing public function, or add another CSS correction for the same selector and property.

- `studio-navigation.js` owns routes and browser history. After a current navigation completes it calls `StudioShell.sync(view)`; the shell does not wrap the router or register a second history listener.
- `studio-shell-nav.js` owns the shared frame, breadcrumb and navigation.
- `app.js` owns the public workspace and conversation entry points. `StudioWorkspace` in `workspace-outlook.js` supplies workspace selection and list rendering through explicit delegation. Permissions, reply presentation and attachments are invoked directly; their scripts do not replace the entry points.
- Page-specific image recovery takes precedence. The shared `media-fallback.js` recovery handles unowned images and exhausted fallback attempts, without detaching a page-owned image before its handler runs.
- `studio.css` is the single canonical shared stylesheet and retains the existing cascade priority and responsive conditions. Superseded declarations and retired layout controls have been removed. Remaining `!important` declarations preserve existing shared/theme behavior; they are not an invitation to append overrides. Jobs and Job Details retain their dedicated component stylesheets.

The cleanup was compared against main commit `d5b4dbcc7aff25048715be5d1e6c993b77c16d92`: 30 Chromium screenshots across Today, Workspaces, Front Desk, Media, Messages, Jobs and Pipeline, plus open workspace and Job Details, match within antialiasing tolerance. Computed styles and geometry match apart from discarded grid settings on elements that always use flex layout. Four viewport sizes include a short desktop viewport. Route destinations, labels, permissions and API contracts were preserved. Live Kratos/backend deployment was not exercised.

Run the UI suite with `node --test --test-force-exit *.test.cjs` from `tests/ui` after installing its dependencies. The force-exit option prevents existing failing tests from leaving browser timers running. Focused cleanup, preview, Jobs and Job Details checks pass. The broader suite still has ten existing failures outside this cleanup; those checks require separate correction and are not a green deployment gate.


## Automated enforcement (transition baseline)

Run `node scripts/check-ui-css-ownership.cjs` on a branch with `origin/main` fetched. GitHub Actions checks pull requests. The legacy stylesheet was removed; `studio.css` is the only shared base stylesheet loaded by `index.html`. The guard rejects reintroduction of legacy references, new `!important` declarations and override-patch patterns. Existing `!important` declarations remain technical debt. Continue migrating component-specific styles into their owning files without duplicate declarations. The check now compares selector/property ownership against main and fails PRs that introduce additional competing declarations, page-scoped shared-shell positioning, legacy CSS references, new !important rules, or override patches. Existing debt is grandfathered, not endorsed. Browser regression testing remains mandatory. Require the `css-ownership` status check in branch protection to prevent bypass.\n

## Mandatory shared-shell CSS rules

- The header, logo, page identifier, navigation, breadcrumbs, session and attention drawers and footer are **one shared shell**. Page styles cannot reposition or resize those elements.
- Before changing a shared-shell style, inspect every loaded stylesheet for declarations targeting that component. Edit its existing owning rule, and remove obsolete declarations rather than layering more specific selectors.
- Never use `!important`, a new body/view qualifier, an ID qualifier, or a responsive duplicate to defeat an existing shell declaration. Responsive differences must be intentional and limited to genuinely responsive properties.
- Every UI PR must pass `css-ownership` and the shared-shell ownership test. Configure GitHub branch protection to **require** the `css-ownership` check before merging; a workflow by itself is not a merge gate.
- Do not merge until browser verification covers Today, Front Desk, Job Detail, and at least one other page at desktop and narrow widths. All pages must use the same header alignment, and no page may render a separate Today's Sessions card.

