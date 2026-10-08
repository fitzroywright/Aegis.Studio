# Connected Studio workflow — resume checkpoint

Branch: `feature/studio-connected-workflow-20261008`.
Baseline: `f86515d4382dbeb110a407383666b0348964e41d`.
Specification: `docs/STUDIO-CONNECTED-WORKFLOW-BRIEF.txt` (approved revision, October 8).

## Status

- Specification and read-only inspection complete.
- Implementation is in progress on the isolated feature branch; see checkpoints below.
- Main remains unchanged; never merge unfinished work.
- .NET SDK and Common projects are unavailable in this execution environment. Application build/database tests must be run locally; never claim they passed here.

## Next

Implement shared stage history and revision checks, permanent Unassigned workspace, immediate Pending Job intake, session crews, assignments and schedule compatibility; then Production review, monitoring and navigation. Update this checkpoint and commit each completed stage.

## Verification

Document consistency checked. Later validation results and remaining limits are recorded below.

## Recovery

Fetch this branch and read this checkpoint and the specification. Compare branch head with latest main before resuming; preserve all approved decisions. This branch is isolated, so returning to unchanged main leaves the feature out of the running application. Database migrations must preserve prior records and be repeat-safe; branch switching alone does not reverse deployed database changes.

## Checkpoint 2 — foundation in progress

Implemented additive workflow schema, permanent protected Unassigned workspace, stage history/revision columns, elapsed timing rules, distinct source/final approval rule contract, existing notification queue deduplication, immediate Pending Job intake, repeat-safe pending-booking migration, source-scoped intake IDs and deposit recording without automatic confirmation. Added C# timing/transition unit tests (not executed: no .NET SDK).

Next: workflow mutation endpoints and permissions, crew/session scheduling, review submissions and UI. This checkpoint is not a runnable feature release and must remain on the branch.

## Checkpoint 3 — service and interface in progress

Added transactional versioned workflow commands, owner/crew-derived access, draft sessions and multi-person scheduling, source/final submission manifests, notification monitoring/retry and a Job action panel. Front Desk now opens Pending Jobs and explicitly confirms them. Jobs lists include elapsed timing and owner/crew filters.

Verification: edited JavaScript syntax checks pass. No .NET compilation or database tests executed. Pending: close legacy mutation bypasses, validate SQL/C# and complete acceptance tests. These changes are still in progress; do not merge or deploy.

## Checkpoint 4 — legacy routes and review integrity in progress

Centralized legacy lead/lifecycle/pipeline mutations into the versioned service; generic Job writes cannot assign staff or skip stages. Temporary assignment access no longer creates permanent grants. Added immutable approved gallery publication, contribution evidence, upload invalidation, task assignment/progress commands, compatibility shoot synchronization and individual attendance reservations.

Still pending: UI compatibility/version payloads, exception paths, runnable acceptance coverage and validation. Known environment blocker remains .NET SDK/Common dependencies/PostgreSQL unavailable. No merge/deployment authorized or performed.

## Execution interruption — recovery checkpoint

Last verified code commit: `2eebc4ff03c3a9c499ebcd7ca329ade83f6a56b98`. Terminal service became unavailable during the fifth checkpoint save. Later code and tests exist locally but have not been verified as remotely saved. Read `docs/STUDIO-CONNECTED-WORKFLOW-RECOVERY.md` before resuming. The implementation remains incomplete and must not be merged/deployed. Focused local DOM tests passed 5/5 and embedded PostgreSQL checks passed 3/3; native .NET tests remain unexecuted.

## Recovery branch checkpoint — October 8, continuation

Active branch: `recovery/studio-connected-workflow-safety-20261008`. Never merge to main before local build and acceptance.
- Saved Front Desk exclusive claims, release and supervisor reassignment.
- Added cross-workspace transfer request, destination-manager decision API/UI and durable SQL records.
- Destination manager may decide a transfer without pre-existing Job ViewJob grant; access remains limited to the specific pending decision.
- Added required deliverable records, delivery/waiver commands, UI and read endpoint. Both the Delivered transition and Completed transition block outstanding requirements.
- Last verified remote code commit at this checkpoint: `9bd116c23124eea60ab4cdf69501b2a1e3ece141`.
- Not built or tested; no .NET SDK, application database, or Common sibling source available through GitHub-only execution. These changes are unverified.
- Known next work: inspect transfer approval lifecycle (avoid superseding the approved request), validate SQL migration against actual schema, harden upload batch proofs and no-op commands, fix permission edge cases, integrate generic schedule writers, complete end-to-end tests and UI compatibility. Preserve My Work styling.

## Continuation checkpoint — saved October 8

- Added destination-manager transfer-decision authorization independent of existing Job view grants, limited to a matching pending transfer.
- Added live elapsed-stage clock in connected Job panel using the actual `enteredAt` snapshot field; refreshes every 60 seconds while the panel is mounted.
- Rejected repeat delivery and waiver commands for already resolved requirements.
- JavaScript syntax for `wwwroot/connected-workflow.js` parsed successfully in the connector execution runtime. This does not constitute a browser/UI test.
- Last CODE commit `520b07eebb8bd3169b2d0061b94245642bd96e2e`. All changes on recovery branch; main unchanged.
- Remaining: native .NET build/test, SQL migration integration test, booking and legacy route adapters, upload-batch proofs, generic schedule reservation concurrency, full A–T acceptance, permissions and operational smoke tests. Not release-ready.

## Schedule concurrency checkpoint

- Generic ScheduleStore Create, Update and Reassign now acquire sorted per-resource PostgreSQL advisory transaction locks BEFORE conflict checks. The lock key is the same `hashtextextended(resource-id-string,0)` used by connected shoot scheduling.
- Update/Reassign also lock resources previously assigned to the entry, avoiding conflicting resource removals and additions.
- Remote code commit: `91e4bf9ba228fd7dd57cf95e61514daff7e22a40`.
- NOT compiled or database-tested. Still investigate stale entry data and external schedule writers that bypass ScheduleStore.

## Checkpoint: scheduling and upload proof

- ScheduleStore Create/Update/Reassign now acquire transaction resource locks before conflict checks. Code commit 91e4bf9.
- Added upload batch schema, source-review readiness gate and Job UI input for expected/completed/failed files and backup confirmation. Code commits 517b48f, 4f88e3f, 8a85d48, fb87ddc.
- JS syntax passed. .NET build, database migration and integration tests are unexecuted.
- Important gap: upload counts are operator-entered; reconcile them with actual stored-file manifests before production use.

## Owner assignment guard

- Owner assignment now rejects an unchanged owner rather than incrementing workflow revision or notifying again. Code commit e53e89a.
- No local .NET build or database integration tests have been run. Main remains unchanged.

## Notification and transfer continuation checkpoint

- Destination-manager transfer approval idempotent retry now accepts the original operation ID without an existing Job view grant, while verifying the pending transfer. Commit e299b80.
- Workflow notification fanout excludes explicitly inactive employees, without suppressing active staff marked nonassignable. Commits 429237c and f192a98.
- Workflow monitor retries durable no-recipient failures and now cancels Pending workflow email if the employee becomes inactive or email preference/destination changes. Commit 6846a97.
- Unverified: pending cancellation assumes studio_notifications supports status Cancelled; confirm notification delivery worker honors it. Native build and database tests not run.

## Notification enqueue boundary checkpoint

- NotificationDeliveryWorker now revalidates Studio Job email recipient activity, email opt-in, and current destination immediately before external queue insertion; cancelled notifications are not retried. Code commit c1d5fd6.
- This complements the one-minute workflow monitor reconciliation, but already-leased external messages may still need downstream cancellation support.
- Build and integration validation remain unexecuted; main unchanged.
