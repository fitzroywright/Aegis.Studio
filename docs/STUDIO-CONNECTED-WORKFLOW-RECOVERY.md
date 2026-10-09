# Interrupted implementation recovery — October 8, 2026

Branch: feature/studio-connected-workflow-20261008
Main baseline: f86515d4382dbeb110a407383666b0348964e41d
Last verified CODE checkpoint: 2eebc4ff03c3a9c499ebcd7ca329ade83f6a56b98
This recovery note is a later documentation-only checkpoint.

## Current interruption

The execution service stopped accepting terminal operations during the fifth checkpoint save. Several attempts returned transport-disconnected errors; simple pwd operations then hung. The fifth save also encountered a truncated JSON read and did not update the branch. Do not claim the fifth checkpoint code is remotely saved.

The shared workspace most recently contained:
 /workspace/scratch/5976ea756718/Aegis.Studio

Preserve this directory before any checkout/reset. It contains uncommitted work beyond the last code checkpoint. Latest export:
 /tmp/studio_save_0.json through /tmp/studio_save_7.json
These are complete tree entries, generated after focused UI tests passed. They are local and their survival is not guaranteed.

## Work present after checkpoint 4, pending successful remote save

- Two-manager workspace transfer requests/approvals and temporary read access for destination managers.
- Required digital/physical deliverables, publication as an independent handoff, delivery per requirement, closure after required tasks.
- Explicit withdrawal, reshoot, no-show, reopening gates and owner-removal/resume handling.
- Equipment and individual-attendance reservations, crew assignment history, sequential gate history.
- Reviewer authority removed from JobOwner-derived grants; employee inactivity respected in assignment and notification handling.
- Latest-output quality checks pin reviewed_version_id independently of source version.
- Old two-person scheduling entry points routed to the complete crew editor; unsafe legacy shoot mutations return a clear conflict.
- Front Desk and old task/detail/pipeline UI adapters send captured revisions and idempotency IDs.
- Connected workflow UI shows required delivery, transfer approval and administration of thresholds/audiences.
- Added native C# integration acceptance tests, updated old deposit/lifecycle tests, DOM and embedded PostgreSQL SQL tests.

Important files changed/new locally:
 Aegis.Studio.Web/Workflow/WorkflowStore.cs
 Aegis.Studio.Web/Workflow/WorkflowSchema.sql
 Aegis.Studio.Web/Workflow/WorkflowEndpoints.cs
 Aegis.Studio.Web/Operations/JobAccessService.cs
 Aegis.Studio.Web/Operations/StudioEmployeeDirectory.cs
 Aegis.Studio.Web/Production/OperationalWorkEndpoints.cs
 Aegis.Studio.Web/Production/OperationalWorkStore.cs
 Aegis.Studio.Web/Production/ProductionItems.cs
 Aegis.Studio.Web/Scheduling/ScheduleStore.cs
 Aegis.Studio.Web/WorkDay/WorkDaySchedulingService.cs
 Aegis.Studio.Web/wwwroot/app.js
 Aegis.Studio.Web/wwwroot/connected-workflow.js
 Aegis.Studio.Web/wwwroot/production.js
 Aegis.Studio.Web/wwwroot/scheduling.js
 Aegis.Studio.Web/wwwroot/studio-navigation.js
 Aegis.Studio.Web/wwwroot/studio-workspace-actions.js
 Aegis.Studio.Web.Tests/ConnectedWorkflowIntegrationTests.cs
 Aegis.Studio.Web.Tests/FrontDeskBookingRequestTests.cs
 Aegis.Studio.Web.Tests/JobLifecycleRulesTests.cs
 tests/ui/connected-workflow.test.cjs
 tests/workflow/package.json
 tests/workflow/schema.test.cjs

## Observed validation, not a release claim

- Focused DOM suite: 5 passed, 0 failed. Administrator controls; three-person crew/attendance/equipment; conflict preservation and same-operation retry; ordinary photographer controls; required-delivery selection.
- Embedded PostgreSQL (PGlite 0.5.8) suite: 3 passed, 0 failed. Actual additive migration repeated safely; protected Unassigned; unknown historical clocks; actual command SQL prepared against application schema; no-op clocks; stale-revision rejection; notification queue deduplication.
- SQL syntax parsed using pglast.
- Changed C# syntax parsed using tree-sitter, which is NOT C# compilation.
- Original broad UI suite: 32 passed, 9 failed. Initial modified broad run: 31 passed, same 9 pre-existing failures plus a Front Desk fixture expectation needing revision. No later full-suite run completed.
- Native .NET build/unit/integration tests UNEXECUTED: no .NET SDK/Common sibling projects/native PostgreSQL available. Do not present parser or embedded SQL checks as those tests.
- Local test logs: /tmp/studio-focused-ui-tests.log, /tmp/studio-workflow-sql-tests.log, /tmp/studio-baseline-ui-tests.log.
- npm UI dependencies installed; tests/workflow dependencies copied into an ignored real node_modules directory. No generated dependency directory should be committed.

## Resume instructions

1. Recover terminal access and preserve current files/export before re-fetching or resetting.
2. Inspect git status and this note. Branch commits were created through the GitHub connector, because shell clone/push lacks credentials. Local history is a synthetic text snapshot; NEVER push that history.
3. Save pending local work using GitHub create_tree/create_commit/update_ref with compare-and-swap expected branch SHA and the latest remote base tree. Preserve binary files by using the real remote base tree.
4. Avoid truncated tool output: split each exported JSON STRING into <=15,000-character pieces, read each, concatenate in functions storage, then parse it. Batch-sized files can still contain one long source file; max_output_tokens alone is insufficient.
5. Update progress markdown and make a local checkpoint commit only after successful remote save. No merge/deploy.
6. Finish known gaps: serialize reservations across generic ScheduleStore writers; explicit upload-batch expected/completed/failed file proof; notification retry/reconciliation and permissions revalidation; no-op assignment policies; claim release/reassignment; correct legacy automation adapters/status mappings; richer request redirection/session selection; live Jobs clock refresh; acceptance/report and route mapping.
7. Run native C# build/tests with correct Common dependencies and STUDIO_TEST_POSTGRES. Investigate baseline UI failures without broad visual redesign. Verify actual administrator mock login, HTTP permissions, and calendar/WorkDay session behavior.
8. Record A–T acceptance results separately as passed, unexecuted, incomplete or deferred. Business-hours/holiday/pause clocks and advanced deposits remain deferred. Preserve Today styling.

User authorized implementation and routine choices. User required an isolated branch and regular saves. Main has not been merged, deployed or changed by this work.
