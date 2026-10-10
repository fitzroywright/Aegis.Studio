# Resumable implementation checkpoint

Brief: Catalog, Job Templates, Work Order Templates, Production and POS Integration (2026-10-10).
Original main: `4a1584bd8a943b216c817f737ae208207ab03224`.

## Saved steps

1. Audited the catalog, financial engine, hard-coded template definitions, task/production stores, WorkDay, galleries, media versions and message imports. Initial audit saved on main in `2d9d90a1e518a2bf728eb64bb027ca0d9375726d`.
2. Added catalog metadata, immutable managed templates, per-line snapshots/work orders and existing-task dependency/QC guards. Local checkpoint `b281fda`.
3. Added management screens, production queues and contextual Front Desk POS windows. Local checkpoint `a304646`.
4. Added connected tests and launch authorization; remote code checkpoint `c8c11b72c19f52962b317848dc87189892228a8b`, branch `checkpoint/catalog-production-20261010`.
5. Validated existing task assignment/completion, fixed agreement fulfillment and variation/refund handling, added bounded approved-source lookup and audited specification amendments. Fixed observed fresh-start audit/schema and activity-reader issues. Final changes are saved in the branch history and main.

## Recovery and remaining validation

The code, tests, actual-app acceptance harness and detailed results are committed. Continue from main; the checkpoint branch retains the implementation recovery point. Read `docs/CATALOG-PRODUCTION-VERIFICATION.md` before changing the implementation.

Outstanding validation: native PostgreSQL concurrent-session/WorkDay regression, browser visual QA, and configured external Customer Gallery publishing. The full UI suite has 13 baseline failures documented in the verification report. Do not describe these validations as completed.

Prior local workspace: `/workspace/scratch/24d145d84380/Aegis.Studio`, with Common sibling projects. SDK used: `/workspace/scratch/77eb8ffe420b/tools/dotnet/dotnet`, 10.0.401. Restore required `-m:1 -p:RestoreUseStaticGraphEvaluation=true`; normal recursive restore failed without useful diagnostics. Build used `--no-restore -m:1 -p:BuildInParallel=false`. A temporary PGlite socket provided disposable PostgreSQL-protocol checks. These runtime paths are session-specific; use the normal .NET/Common layout when resuming elsewhere.

No external deployment, real financial transaction, destructive gallery cleanup or deletion of historical data was performed.
