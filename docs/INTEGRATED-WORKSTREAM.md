# Integrated Studio workstream

Built on main `cc881bc97040e2122d816a122f6b106ce8d7bea3` against the supplied Complete Integrated Workstream Implementation Brief. The established shared shell and approved screen renderers remain authoritative; no corrective CSS or second screen renderer was added. The former Job-specific shoot dialog delegates to the same embedded scheduling dialog as Production.

## Shared records and service boundaries

- Jobs retain their original IDs, conversations, task/activity model, booking records, POS sales and signed financial events. Participating workspace links extend existing workspace permissions without copying Jobs.
- `WorkDaySchedulingService` owns shoot creation, rescheduling, conflict checks and cancellation over the existing schedule and shoot tables. Studio embeds availability and assignment controls. Rescheduling retains shoot/schedule IDs, updates photographers and appends assignment history; the mobile WorkDay experience reads the same schedule.
- Existing operational activities contain traceable production items. Approval pins an exact version ID. Uploading a later revision leaves existing production sources unchanged. QC records produced quantities, passes, rejection reasons and audit history. Rejections return work to ReworkRequired. Completion checks unfinished production before Ready for Client.
- Completed POS lines requiring photographic outputs create production work. Their quantity, product, Job, sale line and exact approved source remain linked. Missing source assignments block production until a worker chooses an approved image. Refunds reduce unproduced quantities, cancel fully refunded work and expose already-produced refunded quantities for stock/waste review.
- Front Desk partial balance payments use the existing POS sale/tender/payment mechanism. New product sales and their receipts balance separately from existing package charges. Sale/refund finance events use stable IDs. A refund retry replays its operation ID. Collection/delivery requires readiness and a settled Job balance, records the recipient and history, and only then completes the Job. Invoice status cannot be manually set to Paid.
- Finance filters/export use the same signed events, methods and staff references. Management drills into receipt and exception records. Admin exposes permission-protected storage, preview queue, delivery and provider status without credentials or private paths. Common.Storage, Common.Messaging and Common.Diagnostics remain the supporting implementations.

## Photographic scale and resilience

- Gallery queries filter in SQL and use UUID keyset pages (100 default, 200 maximum). Subjects have server search and a bounded list. All-matching classification runs as a logical SQL selection, rather than downloading all IDs. Today links can target an asset outside the first page.
- Workspace, Production and Management overview cards fetch aggregate counts and a handful of thumbnail IDs rather than every gallery. Authorization, finance and workspace links are batched rather than read once per Job. Invoice lines are batched. Today retains progressive startup and timing instrumentation.
- Version writes verify Common.Storage bytes before committing references. Every insertion queues a derivative through a database trigger. A durable leased queue, incremental backfill and one decoder per process generate 384-pixel thumbnails and 1600-pixel previews in Common.Storage. The grid uses thumbnails and detail uses previews; original downloads remain separate. Unsupported previews retain readable originals. Failed previews have retry controls. Multiple worker processes coordinate with SKIP LOCKED.
- Uploads are sent progressively, one file per request, with two ingestion slots per process and bounded waiting. Exports spool to temporary disk and remain bounded. This bounds resource use; the 100,000-asset metadata test does not establish 100,000-photo daily decode/ingestion throughput on production hardware.
- Demo downloads retry rate limits/transient provider failures. Verified seed-owned orphan files from interrupted database transactions can be reused. Existing missing-file/sidecar recovery remains active. Rerunning the loader preserves stored IDs and user edits; it does not reset the database.

## Connected demo and photography

The existing 27-Job universe remains shared across screens. Demo portrait sources are a reviewed, flag-free civilian sample from Wikimedia Commons. Title, file page, license and credit are embedded in `Demo/portrait-catalogue.json`. Source URL/page/license are retained in storage metadata. Large libraries retain their asset counts and stable identifiers while reusing the smaller source set; these asset counts do not represent distinct source photographs. Revisions retain the asset photograph. No generated photographs are used. The portrait sample illustrates fictional subjects, not the people named in the Job roster.

Campion Graduation includes 327 subjects and 241 real photographs with traceable production/QC units. Jobs also show subjects awaiting capture, retakes, completed work and client handoff. The v7 additive upgrade links participating workspaces, pinned approval batches, actual POS sales/partial refunds and completed collection records. An approval example has v3 pinned before a later v4 upload. Older healthy v6 demo photos remain retained, including historically repeated photos, to preserve existing histories and edits; fresh loads use the expanded catalogue.

First-time download speed depends on Wikimedia and local connectivity. HTTP rate-limit backoff can delay the progress meter; it reports the actual phase and file, and finishes only after database commit. A failed/interrupted load can be retried. Preview generation continues in the background after seeding.

## Validation and local runtime cycle

Release solution build: zero warnings and errors. Tests: 179 .NET tests passed (176 Web and 3 Domain), including all 11 PostgreSQL scenarios, with no skipped tests; 33 UI tests passed. PostgreSQL tests run against an isolated disposable PostgreSQL 16 database, not the user's data. They cover 100,000 metadata assets, bounded/disjoint pages, 50,000-image logical classification, approved-v3 preservation after v4, actual POS output/refund linkage and replay, real-photograph thumbnail generation, unsupported-format original retention, demo-upgrade idempotency and shared scheduling/conflict rollback. Independent sale-line quantities, partial balance payments and refund retries, existing booking, permissions and interrupted-demo tests also run with PostgreSQL enabled. The resumed standard build and test results supersede the earlier 172-case checkpoint; recovery details are in `RECOVERY-VALIDATION.md`. UI tests cover the eight screens, bounded thumbnail pages, pinned QC/rejection controls, finance filtering and progressive startup.

From the repository root, with the .NET 10 SDK and the configured Common checkout:

```powershell
dotnet build Aegis.Studio.slnx -c Release
dotnet test Aegis.Studio.slnx -c Release
npm --prefix tests/ui ci
npm --prefix tests/ui test
```

To include transactional tests locally, set `STUDIO_TEST_POSTGRES` to a disposable PostgreSQL test database before `dotnet test`; each test creates/drops its own schema. Never point it at an operational database.

Your local runtime cycle still needs the real Studio configuration: directory sign-in/roles, storage permissions/provider, optional messaging/gallery/device adapters, live first-time photo downloads and visual inspection in your browser. No external delivery, payment-device or production-ingestion throughput result is claimed from the isolated tests.

