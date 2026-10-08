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
