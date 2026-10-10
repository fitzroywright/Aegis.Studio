# Job Details cleanup — 10 October 2026

This records the initial cleanup. The current approved layout and ownership rules are documented in [JOB-DETAILS-APPROVED-LAYOUT.md](JOB-DETAILS-APPROVED-LAYOUT.md).

Base: main at 96a5ebc7734bc5178ee09fa8380f9857c7e92808.

The previous layout moved Subjects into the right rail but subsequently searched
the media column for its content, Add Subject button and navigation destination.
Opening a job threw a null error before the remaining tabs and reply handler were
bound. A swallowed asynchronous failure also left Subjects empty.

The replacement creates five dedicated center panels in their final containers:
Media, Details, Subjects, Tasks and Activity. Messages retains its post-it card on
the right; its navigation button focuses the reply field. Panels load on demand,
surface errors with Retry and reject stale responses. The header shows compact
workflow state, owner and time in stage; Assignments & Workflow opens the existing
permission-aware workflow controls in a scrolling dialog. Workflow clocks are
disposed when the job view is replaced.

Obsolete Job Details CSS blocks, hidden task rules, fixed clipping heights and
unused attachment rendering have been removed. job-details.css owns the page's
viewport layout. Collection filtering uses the actual media toolbar rather than
hardcoded collection shortcuts. Job Details uses the shared Studio shell, shows
the correct header and footer count, and resolves the application lexical
workspace cache for breadcrumbs and My Workspaces. Changed scripts/styles have
new cache versions in index.html.

Validation:

- Eight new DOM regression tests pass, covering opening, replying, subjects,
  permissions, panel navigation, retry, stale responses, workflow controls,
  subject-photo navigation and the lexical workspace cache.
- The existing job layout/reply test passes with the replacement structure.
- Local Chromium viewport checks: 1440x900, 1280x720, 1024x768, 800x600 and
  390x844. No document scrolling, horizontal document overflow or JavaScript
  errors were observed. Details, Subjects, Tasks and Activity each show their
  own panel. Full workflow controls are visible in the dialog.
- Full UI suite: 43/54 pass. The remaining 11 failures also occur on the unchanged
  baseline (34/46 pass, 12 failures including the now-corrected Job Details test).
  Those failures concern existing Front Desk, Production, Today startup,
  navigation and outdated structural assertions.

Only browser code, CSS and UI tests changed; no .NET backend build was required.
Live deployment verification and investigation of jobs with zero stored media
remain separate from these simulated API and viewport checks.
