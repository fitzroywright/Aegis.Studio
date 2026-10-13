# Studio startup data-write audit — 2026-10-12

Audited main: 9547e407b9189dda7a76198407828f50815f64fd.

Scope: Program initialization, schema initialization reached through endpoint registration, schema work in constructors, embedded SQL, and background-worker backfills. Static inspection only; no .NET build or PostgreSQL execution was available.

## Already removed on main

- StudioDatabase and StudioFeatureDatabase: assigned_to -> job_lead backfills.
- WorkspaceJobStore.EnsureSchema: scan all jobs and insert Unclassified subjects.
- MediaProductionStore.Migrate: insert collections/galleries from existing jobs/assets and update subject gallery_id.

## Remaining business-data backfills on main

| Location | Startup behavior | Consequence |
|---|---|---|
| StudioFeatureDatabase.InitializeAsync | Convert studio_production_tasks into studio_job_activities, then drop old table | Inserts into a guarded child table; can crash with closed jobs; deletes legacy table |
| StudioFeatureDatabase.InitializeAsync | Delete shoots whose schedule_entry_id is null, then set NOT NULL | Can trigger closed-child guard; silently deletes records |
| WorkflowSchema.sql version 2 block | Create historical workflow history and additional crew from existing schedules | Crew inserts are guarded; can crash for a closed job with existing workflow sessions |
| WorkflowSchema.sql owner assignment block | Create assignments from job_lead and update owner_assignment_id | Open-job filter avoids closed jobs but still changes business data during startup |
| Program -> FrontDeskBookingRequestStore.MigratePendingJobs | Create jobs for unlinked requests, copy deposits, rewrite dates/history, delete generated notifications/failure records | Startup performs business transitions and ledger writes |
| WorkDayStore.EnsureSchema | Copy legacy assignment status to per-photographer state | Startup invents new per-photographer state from old records |
| StudioEmployeeDirectoryStore.EnsureSchema -> RefreshFromStudioSources | Import role members, photographers, and workspace members into employee directory | Implicit employee creation; also runs from GetAll, so reads can write |
| StudioAccountsStore.EnsureSchema in ManagementAccountsPortal.cs | Backfill invoice/job link table from invoices | Hidden invoice relationship migration |

## Configuration changes on startup

- GlobalOperationalRoleStore.EnsureSchema repeatedly initializes default roles and permissions.
- EnsureRole uses ON CONFLICT ... DO UPDATE and sets is_active=true: explicitly disabled built-in roles are re-enabled, descriptions replaced, update timestamps changed.
- EnsurePermission restores missing default grants each startup.
- Configured administrative group mappings are applied on startup.
- Six old role-permission columns are dropped from studio_global_operational_roles.
- These are configuration/bootstrap concerns, separate from job backfills; default initialization must distinguish a new role from an administrator's existing configuration.

## Writes that serve current operations rather than compatibility migration

- Required Unassigned workspace, Accounts/Finance workspace, and initial workflow thresholds.
- WorkDay location retention applies the configured retention period.
- MediaDerivativeWorker pages existing versions to queue missing previews. It does not rewrite job/subject assignments and catches failures in its background loop. Removing it without an explicit preview-rebuild alternative would change media behavior.
- Notification, escalation, webhook, workflow, and preview workers process their normal queues/events.
- Stored trigger bodies contain INSERT/UPDATE/DELETE statements but those bodies execute on business events, not simply because CREATE FUNCTION runs.

## Additional dependency uncovered

StudioQuoteStore.Convert inserts a Front Desk request without creating its Pending Job. The startup MigratePendingJobs routine later supplies that missing job. This is a real normal-operation dependency on a startup conversion, not on demo data. Quote conversion must create its Pending Job transactionally when conversion is requested; do not rely on a later restart. This must be addressed before treating removal of MigratePendingJobs as functionally complete.

## Schema-only initialization inspected

Messaging database and unified messaging schema; message attachments; workspace/job schema after the removals above; scheduling operational schema; commercial production schema; production-item/order schema; versioning; clients; quote table schema; scoped security roles; workflow automation table schema; escalation table schema; employee-directory table creation; accounting/register schema; public API/webhook schema; galleries/access schema; requester notification preference schema. Their DDL is separate from the writes called out above.

## Delivery status

PR #129 contains the original broader cleanup, which has not merged. It does not cover the newly discovered employee import, invoice links, role reset, or quote-conversion dependency. It requires updating and local verification before merging. Automatic review rejected its main update and merge because broad startup changes were untested. Current main contains only the narrow job-lead, subject, and media startup fixes.

## Verification required

Use a disposable native PostgreSQL database. Test startup with completed/cancelled jobs and legacy-shaped records. Confirm repeat startup does not rewrite operational records. Separately prove fresh installation and booking -> assignment -> media -> production -> delivery with zero demo records, plus quote conversion creates its job immediately.
