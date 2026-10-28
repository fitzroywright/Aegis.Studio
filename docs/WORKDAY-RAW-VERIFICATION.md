# Issue #386 local verification

Performed on the developer Mac in an isolated worktree based on main `136ebd4` (PR #385). The user's original checkout and existing worktrees were preserved. No AWS/Kratos deployment, production credential/configuration change, restart, merge, database migration against production, or production account operation was performed.

## Passing checks

- Node 24: `npm ci`, `npm audit` (zero vulnerabilities), `npm test` in PublicSite: **71 passed**, none skipped. This includes existing Google authentication, JPEG gallery, booking, watermark, and shoot-completion regressions.
- RAW API acceptance: actual 250 MiB streamed transfer, out-of-order/duplicate chunks, mismatched chunk/full checksum, interrupted stream, restart during transfer, restart after original commit/before queue insertion, malformed RAW, wrong assignment/subject/user, private original access, quota, concurrency saturation/retry, cancellation, preview failure, verified ACK, Conflict/replay safety, positive grace and purge.
- Studio Debug build/test compilation and Release build succeeded with no compiler warnings/errors. The targeted transfer/import/manifest tests: **22 passed**, none skipped, against a disposable local PostgreSQL 18 schema per test. A real 250 MiB disk-backed DNG was imported, read through both the canonical attachment and immutable version stores, and checked byte-for-byte by SHA-256. Other tests cover bad hash/truncated private pull, invalid direct Studio RAW, import retry, durable receipts, mobile chunk resume after disconnect, and decoder credential isolation.
- Existing WorkDay DOM interaction tests: **4 passed**. Canonical gallery/drop/picker renderer and stylesheet ownership were preserved; no CSS was added or changed.
- Real headless Google Chrome, `tools/test-workday-raw-browser.mjs`: passed incremental worker hashing, failed XHR after first chunk, reload/reselection resume without resending confirmed chunks, RAW plus JPEG selection, actual progress, gallery persistence on reload, explicit server cancellation, pause/removal, and sign-out during hashing; no JavaScript errors. It creates and removes only disposable local accounts/storage.
- Python: **14 passed** across the existing PR #101 RAW preview tests and new bounded container tests. Structural container tests cover all nine requested RAW extensions, wrong vendor, bounds/dimension abuse, malformed directories/media boxes, and invalid signatures. Real upstream [Nikon NEF](https://github.com/letmaik/rawpy/blob/main/test/iss030e122639.NEF) and [Canon CR2](https://github.com/letmaik/rawpy/blob/main/test/RAW_CANON_40D_SRAW_V103.CR2) files were accepted by the probe and rendered with the unchanged canonical `raw_preview.py`: JPEG previews 1600×1065 and 1600×1064. These samples were kept outside Git.
- `git diff --check` passed. Reviewed the full diff and searched affected renderer/transport owners; there is one gallery renderer, one selection implementation, and separate protocol implementations for legacy multipart and resumable transfer.

## Required checks still blocked

The PR remains draft. Passing feature checks do not override these release gates:

1. The complete Studio test suite has **16 failures also present on unchanged main**, with identical failing test names and no added failures. They concern fixture schema initialization/missing columns or tables, an old derivative-worker reflection invocation, and a WorkDay roster fixture. The final full suite summary and exact comparison are recorded below. These unrelated fixture changes are not included in this feature.
2. Normal Android Release restore/build fails on the existing `SQLitePCLRaw.lib.e_sqlite3.android` 2.1.2 dependency, **NU1903 / [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)**. Confirmed on unchanged main. No repository audit suppression or package-policy weakening was added.
3. A separate **diagnostic** Android compilation used an exact temporary source copy with only Android selected, and treated NU1903 as a warning solely to reach C# compilation. Both feature and unchanged-main copies fail identically: `LoginPage.cs` CS0117/CS0103 (`Entry.Autocapitalization` / `TextAutocapitalizationType`) and `WorkDayLocationService.cs` CS9113 (unread `device` parameter). This is not a successful Android build. No Android package/device acceptance is claimed.
4. iOS build/device checks are blocked: this Mac has Command Line Tools rather than full Xcode and no iOS workload. Android diagnostic tooling was installed only under `/private/tmp`; the system .NET SDK was not changed. The existing Android SDK/JDK 25 could not satisfy MAUI; temporary JDK 21 and checksum-verified Android 36 platform files were used for diagnosis.
5. The broader CSS ownership suite has **4 baseline failures**, reproduced on unchanged main (references to retired `studio-legacy.css` plus stale selector expectations). No stylesheet is changed by this PR.
6. Docker image build was not run: Docker is not installed/available on this Mac. The Dockerfile now uses repository-root context to include the canonical Python helpers and pinned preview runtime; validate the image before container deployment.
7. Physical mobile offline/background/kill/restart behavior, native file-picker behavior with real large camera originals, and a connected Studio-worker/API acceptance journey still require device/integration verification. The mobile transfer Core protocol, streamed pull, durable import, and ACK/retention protocol have local automated coverage, but are not a substitute for that journey.
8. Real-camera decoding has been exercised for NEF and CR2. The other requested families have container-level coverage; model-specific CR3/NRW/ARW/RAF/ORF/RW2/DNG and HEIC codec samples remain release acceptance work. Unsupported previews retain originals as designed.

## Reproduction

PublicSite:

```sh
cd Aegis.Studio.PublicSite
npm ci
npm audit
npm test
```

Studio, from the repository root, with `STUDIO_TEST_POSTGRES` pointing only to a disposable local database and `CommonRoot` pointing to the existing sibling Common checkout:

```sh
dotnet build Aegis.Studio.Web/Aegis.Studio.Web.csproj -c Release -m:1 -p:CommonRoot=/path/to/Common
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -m:1 -p:CommonRoot=/path/to/Common
```

Targeted filter:

```text
FullyQualifiedName~RawPhotoTransferTests|FullyQualifiedName~WorkDayPhotoImportTests|FullyQualifiedName~UploadAndDepositDeclarationTests
```

Install the pinned `Aegis.Studio.Web/Media/raw-preview-requirements.txt` into a temporary Python environment, then run:

```sh
python -m unittest discover -s tests/raw-preview -v
```

Browser test (Playwright installed outside production dependencies):

```sh
PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs node tools/test-workday-raw-browser.mjs
cd tests/ui
npm ci
node --test workday-web.test.cjs
```

Local logs and TRX artifacts are under `/private/tmp/aegis-386-*` and `/private/tmp/aegis-386-results/`. No secrets, production database dumps or original camera samples are committed with this report. See [deployment/storage/retention instructions](WORKDAY-RAW-INGESTION.md) for prerequisites and rollout/rollback constraints.

## Full Studio comparison

Final: **434 passed, 16 failed, 450 total**. Unchanged main: 425 passed, 16 failed, 441 total. The failing-name sets match exactly.

| Test | Existing failure |
| --- | --- |
| `Aegis.Studio.Web.Tests.CommercialProductionTests.CatalogToJobToSaleToProductionUsesImmutableVersionsAndOneWorkOrder` | Npgsql.PostgresException : 42703: column v.intake_reference does not exist POSITION: 302 |
| `Aegis.Studio.Web.Tests.DemoIsolationTests.ClearPreservesSecurityAndNormalBookingAndQuoteConversionWorkWithoutDemo` | Npgsql.PostgresException : 42703: column v.intake_reference does not exist POSITION: 302 |
| `Aegis.Studio.Web.Tests.FrontDeskBookingRequestTests.ConcurrentWebsiteRetryCreatesOnePendingRequestAndOnePendingJob` | Npgsql.PostgresException : 42703: column v.intake_reference does not exist POSITION: 302 |
| `Aegis.Studio.Web.Tests.FrontDeskBookingRequestTests.PendingJobExistsBeforeDepositAndConcurrentReceiptReplayDoesNotConfirm` | Npgsql.PostgresException : 42703: column v.intake_reference does not exist POSITION: 302 |
| `Aegis.Studio.Web.Tests.IntegratedWorkflowTests.PreviewGenerationRetainsUnsupportedOriginals` | System.Reflection.TargetParameterCountException : Parameter count mismatch. |
| `Aegis.Studio.Web.Tests.IntegratedWorkflowTests.WorkDayReschedulePreservesIdsAndRollsBackConflicts` | Npgsql.PostgresException : 42703: column er.attendance_start_utc does not exist POSITION: 242 |
| `Aegis.Studio.Web.Tests.JobGraphPersistenceTests.FrontDeskCreatesOneWorkflowWithExplicitServicesAndSharedJoin` | Npgsql.PostgresException : 42703: column v.intake_reference does not exist POSITION: 302 |
| `Aegis.Studio.Web.Tests.ManualPhotographerPersistenceTests.ManualReportsRetainRecorderAndWaitForEveryPhotographer` | Npgsql.PostgresException : 42703: column er.attendance_start_utc does not exist POSITION: 256 |
| `Aegis.Studio.Web.Tests.MediaProductionPersistenceTests.RosterEditingRoundTripAndWeddingClassificationPreserveIdentity` | Npgsql.PostgresException : 42P01: relation "studio_schedule_entries" does not exist |
| `Aegis.Studio.Web.Tests.TodayStartupTests.BulkAccessMatchesIndividualPermissionsForInheritanceOverridesAndGlobalRoles` | Npgsql.PostgresException : 42P01: relation "studio_workflow_crew" does not exist POSITION: 1410 |
| `Aegis.Studio.Web.Tests.UnifiedMessagingPersistenceTests.PersonalInboxKeepsProviderRepliesInOnePrivateThread` | Npgsql.PostgresException : 42703: column "external_message_id" does not exist POSITION: 76 |
| `Aegis.Studio.Web.Tests.UnifiedMessagingPersistenceTests.SharedIncomingReadIsFirstReaderWinsAndNewRepliesReturnToUnread` | Npgsql.PostgresException : 42P01: relation "studio_workflow_crew" does not exist POSITION: 1410 |
| `Aegis.Studio.Web.Tests.WorkDayFieldSyncTests.LostAcknowledgementReplaysOnceAndNewStudioRosterEditsConflict` | Npgsql.PostgresException : 42703: column er.attendance_start_utc does not exist POSITION: 256 |
| `Aegis.Studio.Web.Tests.WorkDayFieldSyncTests.TwoAssignmentsShareRosterRevisionAndCannotOverwriteEachOther` | Npgsql.PostgresException : 42601: cannot insert multiple commands into a prepared statement |
| `Aegis.Studio.Web.Tests.WorkDayRosterJourneyTests.AssignAcceptReportReplayCountAndScheduleOnlyRemainder` | Npgsql.PostgresException : 42601: cannot insert multiple commands into a prepared statement |
| `Aegis.Studio.Web.Tests.WorkDayShootCompletionTests.ImportedPhotosAllowExplicitCompletionWithoutInventingArrivalAndWaitForEveryPhotographer` | Npgsql.PostgresException : 42703: column er.attendance_start_utc does not exist POSITION: 256 |
