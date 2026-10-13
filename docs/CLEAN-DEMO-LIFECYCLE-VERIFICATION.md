# Clean demo lifecycle — implementation and verification

Branch: fix/clean-demo-lifecycle
Implementation commit: 16bf43e652d7d307032719ccd9c263a9745f9fc2
Pull request: https://github.com/fitzroywright/Aegis.Studio/pull/130
Base main: 92e373a8c751b248b4e9551cf9851bc6b2c043f6

## Result

Demo repair means clear and recreate. No startup business-data compatibility conversion remains in the audited initialization paths. Existing media preview backfill and in-place demo photo repair were removed. Current-format scenario generation explicitly creates its required records and checks them before completion.

Clear preserves security/configuration, removes business records and Studio-owned storage, rotates the dataset epoch, and clears only Aegis.Studio's queue/receipts from shared Common.Messaging tables.

Normal requests and mutating background workers share a PostgreSQL advisory-lock boundary with clear/recreation. Progress/auth remain available. Old in-flight notification payloads cannot cross reset, even if a recreated notification reuses a deterministic ID.

Quote conversion now creates its Pending Job transactionally, instead of depending on the next startup.

Photo inputs are preloaded and decoded before destructive work and scenario transactions. Curated sources are tried first; low-resolution RandomUser substitution is removed. Images preserve up to 4096 pixels at JPEG quality 95. Approved-production examples use structured demo provenance rather than a descriptive-note prefix.

Failed partial generation is cleared. A failed storage purge removes broken business references, reports failure, and requires another explicit Clear to finish remaining storage cleanup. An unavailable photo source detected before Clear leaves the existing dataset intact.

## Checks executed

| Check | Result |
|---|---|
| Compile changed application/test source with cached dependency assemblies | Passed; 0 warnings, 0 errors |
| Unit suite without PostgreSQL opt-in | 311 passed, 46 database-dependent tests skipped |
| Embedded PostgreSQL-compatible database/lifecycle suite | 20 passed, 0 failed |
| Focused demo loader / Studio Management / shared Today UI suite | 13 passed, 0 failed |
| Actual Chromium full-script empty-data smoke | Today plus Workspaces, Jobs, Front Desk, Media, Pipeline, Production, Messages, Operations; no JavaScript errors |
| Full UI suite | Main baseline: 138 passed / 49 failed; branch: 138 passed / 49 failed; identical failing names, no new failures |
| Diff whitespace / removal audit | Passed |
| Published Git blob comparison | All 38 implementation files match the tested local files byte-for-byte |

Database/lifecycle checks cover full regeneration with real-photo fixture inputs, template coverage for every generated Job, photo count/storage references, approved production examples, ownership, security-preserving clear, normal booking and quote conversion without demo data, invalid-demo restart preservation, orphan/legacy-row preservation during initialization, broken-media clear, partial storage-purge failure, and notification dataset-epoch isolation.

## Limits requiring local acceptance

The standard restore failed because the NuGet package cache is missing AWSSDK.S3 4.0.103.4 (and full restore could not be completed here). The verification build compiled current source against existing cached dependency assemblies; it is not a proof of a complete dependency restore/build or authentication-plugin staging.

The embedded service does not reproduce native PostgreSQL session concurrency or Npgsql's exact error protocol. These native-only checks are included but not claimed as executed:
- DemoMaintenanceNativeTests.IndependentRequestsAreBlockedDuringResetAndResumeWhenReleased
- ClosedJobStartupTests.ClosedJobsStillRequireAuthorizedReopening
- FullDemoRegenerationTests.NativeCoordinatorReportsFullResetCompletion

Live internet catalogue downloads, the configured S3 provider, and Kratos deployment have not been exercised. The generation test uses the genuine repository photograph fixture to avoid dependence on public photo-host availability.

The 49 pre-existing UI-suite failures remain outside this demo-lifecycle change; the full suite is not green.

## Local verification commands

Use a disposable native PostgreSQL database, not the live Studio database:

```bash
dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore
export STUDIO_TEST_POSTGRES='<connection string for a disposable native PostgreSQL database>'
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build --filter "Database=Native"
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build --filter "FullyQualifiedName~DemoIsolationTests|FullyQualifiedName~DemoDeliveryEpochTests|FullyQualifiedName~FullDemoRegenerationTests|FullyQualifiedName~ClosedJobStartupTests"
```

After deployment, explicitly Clear and Recreate through Studio Management. Validate completion against the configured storage/photo hosts, then restart Studio and confirm startup performs no data conversion. Normal operations must also be checked with an empty dataset after Clear.
