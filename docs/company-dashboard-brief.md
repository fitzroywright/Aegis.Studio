# Company Dashboard implementation brief

Replace Today with the sole company-wide operational Dashboard, using the approved navy-and-gold mockup as the visual target. Jobs remains the detailed job list. Other pages retain contextual counts and filters; do not introduce additional operational dashboards. Financial reporting is a separate future area.

## Dashboard

- Keep the shared header, navigation, breadcrumb and footer under their existing styling owner. Dashboard uses the available content width without the contextual left rail or the large login announcement banner. Retain announcements through their existing accessible entry points.
- Provide six linked summaries: new inquiries, awaiting deposits, needs confirmation, unassigned job owners, sessions today and delivery work due today.
- Below the summaries show today's sessions, prioritized attention, production workload and upcoming delivery work. Use compact tables and lists, real authorized data and the existing production lane projection. Sample figures in the mockup are not implementation constants.
- Count each job once per job summary and each session once per session summary. Use recorded delivery activity deadlines; do not substitute a job's event date for its delivery date. Clearly identify missing dates, unavailable sources and permission limitations rather than showing misleading zeroes.
- The shared workspace selector filters the same Dashboard; All means all work visible to the signed-in user. Links retain workspace context and open the relevant queue, job or schedule. Existing Today bookmarks resolve to Dashboard. Back/forward navigation continues to work.
- Load independent sources progressively, support retry and refresh, and cancel stale requests on navigation. Avoid gallery reads and per-job API calls. Support keyboard access, narrow screens and the existing themes.

## Finance workspace correction

Stop automatically creating Accounts / Finance. Retire the existing system-created workspace without deleting historical jobs, conversations, payments, memberships or audit history. Keep linked IDs intact. Route finance communications to active members of the existing Finance Manager global role; do not expand permissions by automatically granting that role to old workspace members. Do not create a new financial dashboard or change payment rules.

## Ownership, verification and delivery

Replace the canonical Today renderer; delete its obsolete personal-grid implementation and update affected tests. Do not add fallback renderers, corrective CSS layers, selector escalation or new !important rules. Shared-shell layout changes belong to shared-shell code and CSS only.

Verify real summary aggregation, failed and forbidden sources, workspace filtering, queue links, cancellation, browser history, safe finance retirement and finance role routing. Run the CSS ownership guard and relevant UI regressions. Check Dashboard, Front Desk, Job Details and another operational page at desktop and narrow widths. Keep build/test evidence explicit; do not claim a Mac/.NET build that was not run.

Deliver the completed change through GitHub and merge to main after required verification passes. The user has authorized implementation and merge without another continuation prompt. Report the merge commit, checks performed and any genuine blocker; do not describe an unverified or unmerged change as complete.
