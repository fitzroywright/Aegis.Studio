# Studio factory reset - implementation checkpoint

Status: **NOT IMPLEMENTED**. This document is the acceptance contract for the isolated `feature/studio-factory-reset-20261009` branch. Do not deploy or expose destructive reset until all acceptance checks pass.

## User-approved scope

Kratos is a disposable Studio demonstration installation. Remove **all** Studio business records, regardless of demo labels: jobs, bookings, clients, conversations, messages, activities, sessions, schedules, employees' business assignments, financial/demo records and stale seed markers. Remove every workspace and its memberships except the permanent system workspace with `system_key='unassigned'`. Preserve the system workspace identity, authentication, global authorization, database schema, and deployment configuration.

Delete **all** objects under exclusively Studio-owned storage roots, including unreferenced/orphan objects, originals, versions, thumbnails, attachments, and generated derivatives. A shared provider root/bucket must never be erased wholesale; establish the exact exclusive Studio prefix/root before any deletion. Also clear Studio-owned local caches and derivative roots. If any root cannot be proven exclusive, fail closed and report it.

## Required implementation

1. Administrator-only endpoint and UI action `Factory Reset Studio`, separately gated by `DemoData:Enabled` and `DemoData:AllowDestructiveReset`; exact confirmation phrase and clear inventory/preview before execution. Reject concurrent resets and writes while reset is active.
2. Discover and classify Studio tables and foreign keys. Implement ordered cleanup in a transaction, preserving system/security/configuration tables and the permanent Unassigned workspace. Check non-Unassigned workspace count is zero after cleanup. Do not use unrestricted `TRUNCATE ... CASCADE` or disable foreign-key protections.
3. Discover every Studio-owned media root/provider and list all objects, including orphans. Use Common.Storage lifecycle APIs for provider-backed objects; do not assume DB references are exhaustive. Implement durable progress/checkpoints and retry for partial deletion, and verify an empty object inventory before reporting success.
4. Avoid holding a database transaction open during slow remote storage deletion. Use an explicit maintenance state to block writes, and resumable cleanup phases. Storage and PostgreSQL are not a shared transaction; report partial failure rather than success.
5. Clear historical seed markers so fresh generation does not silently skip work. `Generate Demo Data` recreates only current Connected Workflow fixtures; do not invoke legacy seed writers.
6. `Reset and Regenerate` performs the verified factory reset, then generates and verifies fresh fixtures; never report success if storage cleanup failed.
7. Integration tests against a disposable PostgreSQL database and an isolated storage prefix: existing workspaces and unreferenced photos removed; only Unassigned remains; authentication/configuration preserved; failed provider deletion resumes; shared-prefix objects untouched; generation works twice without duplicates.

## Existing code and gaps

- `Aegis.Studio.Web/Workflow/ConnectedWorkflowDemo.cs` currently truncates dependent business tables but explicitly preserves `studio_workspaces` and `studio_workspace_members` and never deletes media.
- `Aegis.Studio.Web/Workflow/WorkflowEndpoints.cs` exposes current admin reset routes and confirmation. Their semantics must be replaced or renamed to avoid misrepresenting a partial reset as a factory reset.
- `Aegis.Studio.Web/Operations/DemoDataEndpoints.cs` contains historical seed writers; ensure they cannot repopulate legacy data.
- `Aegis.Studio.Web/Storage/AttachmentStore.cs` uses `PrefixedFileStorage` for attachments, and `.delivery-cache` is a local derivative path. Other photo/version roots must be inventoried.

**Do not merge this branch or run destructive commands until implementation and validation are complete.**


## Implementation checkpoint (2026-10-09)

Commits on this branch implement a fail-closed storage-first coordinator and wire the admin reset endpoints. The coordinator requires `Storage:FactoryResetExclusiveRoot=true` and both `IStorageQuery` and `IStorageLifecycle` on the configured `IFileStorage`. It purges enumerable keys, re-lists to verify emptiness, clears the local delivery cache, then runs the database reset and optionally regenerates Connected Workflow demo data.

**Not production-ready:** Common.Storage currently implements irreversible `IStorageLifecycle` for local/network filesystem providers, but not S3, Azure Blob or SharePoint. Those providers intentionally reject reset before deletion. Current-key enumeration cannot independently prove there are no orphan historical versions without a provider-level audit. The database reset has not been integration-tested against a representative PostgreSQL schema; protected FK dependencies may correctly block it. No Kratos test or build has been performed. Do not enable the destructive flag on a shared root, and do not merge until these gaps are addressed.


## Dedicated SeaweedFS bucket implementation

The user confirmed that no other application uses Studio's S3 bucket. `Common.Storage` now includes `IStorageBucketReset` and `S3FileStorage.PurgeBucketAsync`, which delete the **entire configured bucket's contents**, ignoring `RootFolder` so orphaned and legacy keys are included. The bucket itself is retained. The purge enumerates S3 versions/delete markers and current objects, deletes them, then checks both inventories are empty. It fails on permissions, unsupported version APIs, or lack of deletion progress. The operation requires S3 permissions to list current objects and versions and to delete objects and versions. Do not assume SeaweedFS supports version listing until verified on the target server.

The Studio reset requires `Storage:FactoryResetExclusiveRoot=true` and `DemoData:AllowDestructiveReset=true` (plus `DemoData:Enabled=true` outside Development). Database reset preflight runs in a rolled-back PostgreSQL transaction before S3 deletion. Configure the explicit exclusive-root flag **only** on the isolated Kratos demo instance after verifying its bucket name. Deploy latest `Common.Storage/main` alongside this Studio feature branch; both repositories must be updated for compilation.

**Validation pending:** build both projects, verify S3 version-list behavior on SeaweedFS, run an integration test with old/current/orphan objects and versions, and prove database reset plus regeneration. No destructive operation has been executed against Kratos. Do not merge or deploy as production-ready without this proof.
