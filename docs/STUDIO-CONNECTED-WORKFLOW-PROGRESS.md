# Connected Studio workflow — resume checkpoint

Branch: `feature/studio-connected-workflow-20261008`.
Baseline: `f86515d4382dbeb110a407383666b0348964e41d`.
Specification: `docs/STUDIO-CONNECTED-WORKFLOW-BRIEF.txt` (approved revision, October 8).

## Status

- Specification and read-only inspection complete.
- No application changes yet.
- Main remains unchanged; never merge unfinished work.
- .NET SDK and Common projects are unavailable in this execution environment. Application build/database tests must be run locally; never claim they passed here.

## Next

Implement shared stage history and revision checks, permanent Unassigned workspace, immediate Pending Job intake, session crews, assignments and schedule compatibility; then Production review, monitoring and navigation. Update this checkpoint and commit each completed stage.

## Verification

Document consistency checked. No application tests executed.

## Recovery

Fetch this branch and read this checkpoint and the specification. Compare branch head with latest main before resuming; preserve all approved decisions. This branch is isolated, so returning to unchanged main leaves the feature out of the running application. Database migrations must preserve prior records and be repeat-safe; branch switching alone does not reverse deployed database changes.

## Checkpoint 2 — foundation in progress

Implemented additive workflow schema, permanent protected Unassigned workspace, stage history/revision columns, elapsed timing rules, distinct source/final approval rule contract, existing notification queue deduplication, immediate Pending Job intake, repeat-safe pending-booking migration, source-scoped intake IDs and deposit recording without automatic confirmation. Added C# timing/transition unit tests (not executed: no .NET SDK).

Next: workflow mutation endpoints and permissions, crew/session scheduling, review submissions and UI. This checkpoint is not a runnable feature release and must remain on the branch.

## Checkpoint 3 — service and interface in progress

Added transactional versioned workflow commands, owner/crew-derived access, draft sessions and multi-person scheduling, source/final submission manifests, notification monitoring/retry and a Job action panel. Front Desk now opens Pending Jobs and explicitly confirms them. Jobs lists include elapsed timing and owner/crew filters.

Verification: edited JavaScript syntax checks pass. No .NET compilation or database tests executed. Pending: close legacy mutation bypasses, validate SQL/C# and complete acceptance tests. These changes are still in progress; do not merge or deploy.
