# Next required actions

Front Desk's All view, Job Details, enquiry dialogs and Needs Attention use the
same server projection at `GET /api/work/next-actions`. Each action includes a
reason, responsible person, due date and optional waiting reason. Front Desk
supports responsible-person and due-date filters; Needs Attention supports all
staff, assigned-to-me and unassigned views. Appointments retains check-in and
session controls. Sales operations remain in the Front Desk action bar.

`NextActionRules` selects the current lifecycle prerequisite or eligible production
graph task. Closed records are excluded. The canonical `StudioNextActions`
renderer and router reread a record before opening its existing workflow,
Session Plan, media, quotation, client-association or deposit control. Displaying
an action does not bypass that control's authorization or prerequisites.

## Responsibility and deadlines

`PUT /api/work/next-actions/{kind}/{id}` requires current edit/production-management
permission for a job, or ReplyRequester permission for an enquiry. The assignee
must be active and assignable in the employee directory. These are coordination
details; assigning responsibility does not grant a role or permission.

`studio_next_action_details` stores the record kind, ID and action key together.
The next step starts with its own details, rather than inheriting the previous
step's deadline or waiting reason. Updates recheck the current step and version,
lock the source record, and atomically save the new version and audit record.
Stale saves return a conflict. The client retains entered details on a failed
save and retains the reply draft after a failed send.

Dates use Jamaica time. A selected due date means 23:59:59 in Jamaica. Missing
dates remain explicitly unspecified. Schema creation is additive and idempotent;
no business records are converted at startup.

## Verification

The resumed Linux workspace built the Web test project with .NET 10 and passed
all 20 NextAction tests: 17 rules cases and three database cases. The database
cases used the PostgreSQL engine in PGlite through a local wire adapter. They
cover persisted details, audit/version conflicts, step changes, inaccessible
records, read-only callers, invalid assignees and external versus internal replies.
This is not acceptance against a configured deployed PostgreSQL service.

The focused UI checks cover matching action content across pages, current-action
reloads, escaped content, assignment conflicts, Jamaica deadlines, retained reply
drafts, enquiry conversion and Front Desk filters. The CSS ownership guard,
JavaScript syntax and whitespace checks pass. The full UI suite has existing
failures on unchanged main; its result must not be represented as all passing.
The final comparison ran 493 candidate cases (377 passed, 116 failed) and 483
unchanged-main cases (367 passed, 116 failed). The failing test names are
identical. Two old expectations were updated for the new tabs and the single
shared next-action renderer. An unrelated history-back case failed under the
first highly concurrent run and passed both its focused rerun and the final
full run with concurrency limited to four.

Chromium inspected fixture-rendered Front Desk and Job Details at 1920x1080 and
the enquiry at desktop and 768x1024. The Front Desk table fits without horizontal
page overflow, the next action fits within the job heading, and the enquiry fits
both viewports. Images and selected API responses are mocked in these fixtures;
this is layout verification, not live authenticated browser acceptance.

Run the UI cases with:

```sh
node --test tests/ui/next-actions.test.cjs tests/ui/frontdesk-reception.test.cjs \
  tests/ui/frontdesk-actions.test.cjs tests/ui/job-brief-attention.test.cjs \
  tests/ui/job-stage-timing.test.cjs tests/ui/job-details-module.test.cjs \
  tests/ui/shared-shell-ownership.test.cjs
```

Run the backend cases against a disposable configured PostgreSQL database:

```sh
STUDIO_TEST_POSTGRES='<disposable test connection string>' dotnet test \
  Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj \
  --filter FullyQualifiedName~NextAction
```

The coordinated Common.Security migration landed on main during merge
preparation. This change was reconciled onto it, preserving the migrated
authorization, API error handling and protected cache behavior. Its updated
interaction fixtures retain all current page dependencies and exercise the
shared server-projected action rather than the removed page-only renderer.
The pre-modernization comparison above remains historical evidence; consult
PR #400's latest security-regression check for the complete integrated .NET,
PostgreSQL 16 and UI results. No tests are skipped by that evidence gate.

Deployment, the user's Mac build, live provider/role checks and production
database acceptance have not been performed.
