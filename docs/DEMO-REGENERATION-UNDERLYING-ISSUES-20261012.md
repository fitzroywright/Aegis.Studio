# Demo regeneration underlying-issues review — 2026-10-12

## Scope and confidence

Reviewed current main source for DemoSeedCoordinator, DemoDataEndpoints, DemoDenseScheduleData, DemoFullStudioData, DemoDataExpansion, DemoPortraitCatalogue, DemoJobPhotoPopulation, DemoIntegratedWorkflow, DemoJobWorkflowGraphs, ConnectedWorkflowDemo, StudioFactoryReset, WorkflowEndpoints, and demo-loader.js; cross-checked the startup audit.

This is source-level review. The uploaded logs prove startup failures in job-lead backfill and Unclassified-subject backfill. No current failed demo-run exception or native PostgreSQL/.NET execution was available. Findings below identify deterministic implementation defects and failure risks; they do not establish one new live exception as the cause of every reported UI failure.

## P0 — Reset clears authorization configuration inconsistently

ConnectedWorkflowDemo.ResetData protects studio_global_operational_roles and studio_global_operational_role_members, but does not protect studio_global_operational_role_permissions or studio_directory_group_roles. Both tables are selected for TRUNCATE.

The reset therefore preserves role identities and direct memberships while removing permission grants and AD group mappings. Demo generation does not invoke GlobalOperationalRoleStore.EnsureSchema or rebuild that configuration. A later restart may restore built-in grants and configured administrator mappings, but that is a hidden dependency on startup and does not reconstruct arbitrary customized mappings.

Impact: loss of effective permissions after regeneration can explain empty or forbidden operational screens. It does not directly explain a JavaScript render exception.

Required correction: preserve security configuration consistently, or explicitly reconstruct a defined fresh configuration within reset/regeneration. Add a reset test checking custom grants, group mappings, and effective permissions before/after; never rely on restart to restore them.

## P1 — New photo metadata and integrated demo selection disagree

DemoDataExpansion.AddImage now writes note:
"Demo photograph from [configured source]. Source URL retained in Common.Storage metadata."

DemoIntegratedWorkflow still selects only versions whose note begins:
"Real CC0/public-domain photograph from Wikimedia Commons."

That older phrase gates the pinned-approved revision example, approved_version_id updates, and production item creation. Newly downloaded seed images no longer satisfy it.

Impact: a successful fresh run can omit intended approved-image/production/QC examples. Some unrelated QC activity rows can still be created, giving the appearance of a prepared example without its production items.

Required correction: use structured source/provenance and scenario identifiers, not descriptive note text, to determine eligibility. Validate approved sources and production links explicitly before completion.

## P1 — Retry behavior overwrites existing demo edits

DemoDataEndpoints.Seed runs every DemoDenseScheduleData batch before testing the full-demo marker. SeedJob uses ON CONFLICT DO UPDATE for job details including status, assignments, client, and event date. Workspace/member seeders similarly reset values.

The loaded branch claims existing edits were preserved, but dense job edits have already been overwritten.

Required correction: distinguish regenerate (explicit reset and recreate) from resume (finish missing stages without resetting completed business scenarios). Keep committed per-stage checkpoints; validate them rather than treating a marker as proof of validity.

## P1 — Reset/generation is multi-phase without a final business validation gate

Dense batches commit individually; rich demo commits separately; photo population commits per job afterwards. Full-demo and integrated-workflow markers are committed before per-job photo top-up finishes.

A failure leaves a deliberately partial but visible dataset. That architecture can support resumability, but current success is just completion of the seed methods: it does not prove required owners, valid workflow stages, template coverage, approved-image references, financial consistency, or media availability.

DemoJobPhotoPopulation reports at least 40 photos based on asset counts and deterministic additions. The target uses existing asset count, not a final count of decodable/reachable photographs across all assets.

Required correction: run an explicit final integrity check and only then publish Completed; retain progress and failure stage instead of resetting the error to 0% / Failed.

## P1 — Demo completeness depends on implicit repair elsewhere

Main still has startup owner-assignment backfills and crew/history reconciliation. Main demo generators populate job_lead but do not create studio_job_owner_assignments / owner_assignment_id consistently. Assignment lifecycle examples can therefore differ before and after restart.

The prior main fixes removed startup-generated subjects and media galleries. The seed generators must explicitly own those records for every scenario rather than silently inheriting them from initialization.

Required correction: create current required relationships during the scenario creation action, and test that a generated demo needs zero startup data repair.

## P1 — Background activity and reset use different coordination boundaries

Coordinator and factory-reset semaphores protect one process. Seeder phases and reset use different advisory locks. Operational background workers and ordinary users are not quiesced throughout external storage purge -> database reset -> regenerated publication.

Source inspection shows possible races and partial visibility; no runtime race was reproduced here.

Required correction: coordinate reset and generation with a database-backed exclusive operation, explicitly pause conflicting workers/writes, and expose a clear maintenance state to clients. Release it on failures, with a persisted recovery state.

## P2 — Photo selection undermines quality and scenario meaning

DownloadDemoPhoto chooses a random starting candidate between the preferred Commons URL and a RandomUser profile portrait. The preferred high-quality photograph is therefore not necessarily tried first. RandomUser can be chosen even when the curated source is available.

Every photo is re-encoded with a 1200-pixel maximum side and JPEG quality 85. DemoJobPhotoPopulation reuses the same first 40 catalogue positions across every job. Its searchTerm argument is not used by the downloader.

Required correction: curated scenario-appropriate photographs first; quality/dimension validation; explicit fallback with visible provenance; original-resolution storage plus separately generated thumbnails; a reusable downloaded catalogue to avoid unnecessary external requests.

## P2 — Network/storage work is inside database transactions

AddImage downloads, decodes, resizes, stores, reopens, and hashes external images while seed transactions and advisory locks are held. Synchronous waits can make a run slow and keep long transactions open. Database rollback cannot undo successful object writes.

There is an orphan-reuse mechanism, which helps recovery, but it does not make the complete reset/generation atomic.

Required correction: download and verify source assets before database scenario transactions; use short transactions for linking durable storage receipts.

## Separate normal-operation defect

StudioQuoteStore.Convert inserts a Front Desk request without creating its Pending Job. Startup MigratePendingJobs later creates that missing job. This is a real-system dependency on startup conversion, not demo data. Fix quote conversion transactionally before removing that migration path entirely.

## What should remain

- Schema creation and additive columns/indexes/triggers.
- Required fresh system configuration with preservation of existing configured choices.
- Normal business actions creating their own related records.
- Explicit demo reset/generation.
- New-version preview queueing; image-generation triggers already enqueue newly inserted versions.
- Configured retention and normal worker queue processing, with maintenance coordination during reset.

No legacy business-data or media backfill is necessary after a complete reset and current-format regeneration.

## Suggested correction order

1. Preserve effective authorization during reset.
2. Fix approved-photo eligibility and validate production scenarios.
3. Remove implicit startup repairs; fix quote conversion and seed owner/subject/media relationships explicitly.
4. Make resume preserve edits and expose the actual failed stage.
5. Add a final demo integrity validator.
6. Coordinate workers and user writes around reset.
7. Improve image quality and transaction boundaries.

## Verification required locally

Against disposable native PostgreSQL and test storage:
- Fresh setup with no demo records completes the normal booking-to-delivery flow.
- Reset preserves custom permission grants and group mappings.
- Regeneration creates owners/assignments, valid graphs, required media/subjects, QC source versions, payments and deliveries without restarting.
- Resume does not overwrite edits.
- Induced network failure leaves an accurately reported resumable stage.
- Every eligible demo job reaches its required valid-photo count.
- A second startup does not insert/update/delete business records.
- Browser tests exercise failed/partial/complete statuses and actual shared helpers rather than masking console errors.
