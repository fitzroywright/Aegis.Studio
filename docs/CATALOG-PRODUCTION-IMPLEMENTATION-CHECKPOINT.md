# Catalog and production implementation checkpoint

Source brief: 2026-10-10, Catalog, Job Templates, Work Order Templates, Production and POS Integration.
Baseline: main 4a1584bd8a943b216c817f737ae208207ab03224.

## Audit completed
- Reuse PosCatalogStore and studio_pos_catalog as the authoritative selling-price catalog.
- Reuse StudioPosStore/Aegis.Pos.Core, receipts, register reconciliation and accounting. Sales already snapshot unit prices; completion and refunds call ProductionOrderLinker.
- Reuse studio_job_activities and WorkflowStore's versioned task/assignment operations. ProductionStore currently exposes hard-coded job templates; it does not contain persisted editable Job Templates or Work Order Templates.
- Reuse approved studio_media_assets and pinned studio_asset_versions, production item QC, existing Job Gallery uploads and message attachment imports. Existing galleries remain in place.
- Reuse WorkDay scheduling endpoints and existing employee validation.
- Missing: catalog metadata/options/bundles and template links, managed versioned Job/Work Order Templates, independent per-line work orders/dependencies, template-based production generation, and Front Desk-only contextual POS windows.

## Progress and recovery
Current phase: repository audit and baseline build verification. No application changes published.
Local workspace: /workspace/scratch/24d145d84380/Aegis.Studio (main inspection snapshot), Common sibling.
Local SDK: /workspace/scratch/77eb8ffe420b/tools/dotnet/dotnet, version 10.0.401.
Restore uses -m:1 -p:RestoreUseStaticGraphEvaluation=true because the normal recursive restore fails without diagnostic errors in this environment.

## Remaining implementation sequence
1. Add additive schema, catalog extension and immutable template versions.
2. Instantiate existing activities from Job Templates and Work Order Templates; link sale lines and approved media; guard dependencies and QC in backend.
3. Add compact management screens, paginated production queues and Front Desk contextual POS windows.
4. Add meaningful pricing, versioning, retry/dependency/QC and launch tests. Restore/build/test locally and record exact results.
5. Verify the full acceptance scenario before publishing application code to main. Do not claim completion while steps remain.

Commit code checkpoints locally and update this document as implementation progresses. Publish only a validated coherent application change; no destructive gallery cleanup.
