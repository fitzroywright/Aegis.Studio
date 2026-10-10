# Studio presentation ownership

Change the current component or owning rule directly. Do not append a replacement renderer, reassign an existing public function, or add another CSS correction for the same selector and property.

- `studio-navigation.js` owns routes and browser history. After a current navigation completes it calls `StudioShell.sync(view)`; the shell does not wrap the router or register a second history listener.
- `studio-shell-nav.js` owns the shared frame, breadcrumb and navigation cards.
- `app.js` owns the public workspace and conversation entry points. `StudioWorkspace` in `workspace-outlook.js` supplies workspace selection and list rendering through explicit delegation. Permissions, reply presentation and attachments are invoked directly; their scripts do not replace the entry points.
- Page-specific image recovery takes precedence. The shared `media-fallback.js` recovery handles unowned images and exhausted fallback attempts, without detaching a page-owned image before its handler runs.
- `studio.css` retains the existing cascade priority and responsive conditions. Superseded declarations and retired layout controls have been removed. Remaining `!important` declarations preserve existing shared/theme behavior; they are not an invitation to append overrides. Jobs and Job Details retain their dedicated component stylesheets.

The cleanup was compared against main commit `d5b4dbcc7aff25048715be5d1e6c993b77c16d92`: 30 Chromium screenshots across Today, Workspaces, Front Desk, Media, Messages, Jobs and Pipeline, plus open workspace and Job Details, match within antialiasing tolerance. Computed styles and geometry match apart from discarded grid settings on elements that always use flex layout. Four viewport sizes include a short desktop viewport. Route destinations, labels, permissions and API contracts were preserved. Live Kratos/backend deployment was not exercised.

Run the UI suite with `node --test --test-force-exit *.test.cjs` from `tests/ui` after installing its dependencies. The force-exit option prevents existing failing tests from leaving browser timers running. Focused cleanup, preview, Jobs and Job Details checks pass. The broader suite still has ten existing failures outside this cleanup; those checks require separate correction and are not a green deployment gate.
