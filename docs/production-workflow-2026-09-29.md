# Production workflow implementation — 29 September 2026

## Implementation map

| Area | Existing implementation reused | Changes |
|---|---|---|
| Workspace | WorkspaceStore, WorkspaceAccessService, existing member editor | Settings dialog, icon/accent/archive controls, contextual avatars; permanent workspace navigation retained |
| Job conversation | Existing thread and client conversation endpoints | Reply identity preserved; email-only Files card supports selection, preview and download; right inset retained |
| Media | StudioVersionStore, WorkspaceJobStore subjects/collection labels | One production gallery per job, immutable asset IDs, Images/Subjects/Collections lenses, settings/default lens, multiple subject associations, status and metadata |
| Import | Existing immutable local version storage | CSV roster upsert by job-scoped Subject ID; custom columns retained; file/folder picker and drag/drop dialog |
| External editing | Existing immutable version history | Bulk ZIP exports, retained manifests, JPEG XMP export identity, duplicate-return protection, stale/unrecognized return queue and explicit matching |
| Tasks and Activity | OperationalWorkStore and audit endpoints | Compact Tasks card, existing task/calendar editor in dialog, header Activity flyout grouped by day |
| Today | Existing assignments and tasks | Assigned media and subject attention queues with focused job/media navigation |
| Clients | Domain.Contact | Persistent clients independent of workspace, nullable job link, FrontDesk client search/edit/history |
| Quotes | Existing invoice line contracts, pending booking requests, notification delivery queue | Draft estimates, queue email, visible delivery status, acceptance reference, idempotent conversion to pending booking |
| Invoices | StudioAccountsStore | Same-client multi-job invoices, invoice-job associations, serialized invoice number allocation |
| Pipeline/WorkDay | Existing scheduling, availability/conflict services and WorkDay bootstrap | Scheduling action moved into Pipeline; daily assignments show location/instructions; GPS flag unchanged |

## Persistence and compatibility

Startup applies additive, idempotent PostgreSQL schema changes. Existing connection settings and data are retained; no reset or database replacement is used.

- `studio_media_migrations` records the media schema version. New gallery, asset, subject association, editing batch/item, return and unmatched tables augment the existing version store.
- Existing subjects gain gallery, production state and custom attributes. Existing asset keys are mapped to stable asset IDs on first access. Versions/files are not replaced.
- Workspace icon/accent columns are additive. The default accent preserves the existing workspace color bands.
- `studio_clients` and nullable `studio_jobs.client_id` preserve legacy client name snapshots. Historical clients are not guessed or merged by name/email.
- `studio_account_invoice_jobs` backfills existing single-job invoices; combined invoices require all jobs to share the same linked client.
- `studio_client_quotes` retains estimate payloads, acceptance references, notification IDs and pending-booking links.
- Version metadata and production workflow updates enlist in one database transaction. Stored file bytes remain immutable.

## Authorization

New routes require authentication. Media reads use ViewAttachments; production mutations and exports use ManageProduction. Workspace appearance uses workspace access/edit permissions. Existing member permission attenuation and role authorization remain in force. FrontDesk client, quote and combined-invoice operations require Administrator or the existing ViewAllJobs + Billing combination. Today filters job access and assignment. Returned identity lookup is scoped to the target job; cross-job tokens enter the unmatched queue.

## Validation

- Complete `Aegis.Studio.slnx` Release build: passed, zero warnings/errors.
- Studio Domain tests: 3 passed.
- Studio Web tests: 130 passed; 2 PostgreSQL-dependent tests skipped without `STUDIO_TEST_POSTGRES`.
- UI behavior tests: 11 passed.
- Media/client/commercial persistence scenario run separately against PGlite: passed. Covers repeated migration, 50-person roster import/update, subject/collection associations, export manifest, renamed return, duplicate return, stale return, manual matching, cross-job rejection, shared clients, combined invoices, quote queuing/acceptance and idempotent booking conversion.
- No Common.*, Control Plane or Cafeteria source changes.

## Practical limits and remaining validation

- Native PostgreSQL concurrency and live browser visual acceptance were not run in this environment. PGlite is not proof of native concurrency behavior.
- Automatic embedded identity currently supports JPEG XMP. Other formats or stripped metadata require explicit matching. Filenames never determine asset identity. No Lightroom/Photoshop plugin is claimed.
- CSV is supported; native XLSX import is not. Export is limited to 200 files / 500 MB, uploads to 100 MB per file. Bulk uploads are queued sequentially by the browser.
- One gallery per job is exposed. Collections reuse existing logical labels; subjects are independent and many-to-many with assets. Capture time is editable UTC metadata; automatic EXIF capture-time extraction is not implemented.
- Quote sending requires the existing Common.Messaging email provider to be configured. Queued is not Delivered; actual delivery status/errors are shown. Live client email delivery was not exercised.
- Existing public booking/Hub, curated publishing, accounting and production-dispatch integrations are preserved; this change does not certify their full live external lifecycle. Media states do not automatically dispatch physical production or publish client galleries.
- The Activity flyout uses the existing operational audit stream. It is not a complete merged archive of every messaging provider event.

## Local Visual Studio / PowerShell checks

```powershell
Set-Location D:\Projects\Aegis.Studio
git switch main
git pull --ff-only origin main
dotnet build .\Aegis.Studio.slnx -c Release
dotnet test .\Aegis.Studio.slnx -c Release --no-build
```

For transactional tests, set `STUDIO_TEST_POSTGRES` to a disposable PostgreSQL test database and run the tests again. Tests create isolated schemas and remove them afterward. Keep the application's normal database configuration unchanged.
