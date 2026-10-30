# PR #387 local verification

All checks below ran on the developer Mac in the isolated `feature/issue-386-raw-ingestion` worktree. The original checkout and existing worktrees were preserved. No AWS/Kratos deployment, service restart, production configuration/credential change, merge, production database migration, or production account operation was performed.

## Studio, Android and connected acceptance

The complete Studio suite now passes: **451 passed, zero failed, zero skipped**, including the connected acceptance test. The 16 backend failures previously reproduced on main have been fixed. Fixtures now initialize the real workflow, scheduling, role, employee and WorkDay prerequisites; use published assignment generations; exercise the derivative worker's real claim/lease transition; and assert the current pending-Job and messaging contracts. Authorization, conflict, replay, closed-Job and checksum assertions remain enabled. The large pre-existing-catalog fixture applies startup migrations before exercising its gallery queries and bulk operations.

Studio Release builds with **zero warnings/errors**. Android arm64 Release builds with **zero warnings/errors**, including trimming and AOT, and produces locally development-signed APK/AAB artifacts. No device installation is implied.

Android uses [sqlite-net-pcl 1.11.285](https://www.nuget.org/packages/sqlite-net-pcl/1.11.285), resolving SQLitePCLRaw core/provider 3.0.3 and SourceGear.sqlite3 3.53.3. Both restore auditing and the explicit direct/transitive NuGet vulnerability check pass with **zero findings**. There is no audit suppression, NU1903 downgrade, NoWarn exception, or weakening of warnings-as-errors. The unsupported MAUI enrollment-entry property was replaced with the supported keyboard flags API; the unused location-service constructor dependency was removed. The existing mobile database path and queue data model are unchanged.

### Connected upload → gallery → ACK → retention

`WorkDayConnectedUploadTests.LargeWorkDayUploadImportsThroughRealWorkerReplaysLostAckAndPurgesOnlyAfterGrace` passed in about **23 seconds** as part of the full suite. It uses a real loopback Node 24 PublicSite process, disposable SQLite/private storage, disposable PostgreSQL schema and actual Studio services. It calls the production `WorkDayApiExchangeWorker.PullUploadsAsync` method; the only injected fault is a lost ACK response. No production hostname is contacted.

1. Create a local active Studio photographer, real assignment and photography session, and grant Job attachment access. Provision/enroll a disposable WorkDay account through the site's authenticated API; publish its assignment snapshot as test setup.
2. Upload an actual **250 MiB disk-backed DNG container** through the mobile Core's resumable protocol. The real Node API validates chunk and whole-file checksums and queues a metadata-only import. Employee credentials cannot fetch the private original.
3. Run the real Studio pull worker. It downloads/validates the original, imports the gallery asset, immutable version, internal attachment and Job message, then encounters an injected HTTP 503 instead of delivering its ACK.
4. Verify that the API operation is still Pending, retention deletes nothing, and Studio's authoritative original reads back with the expected size/SHA-256.
5. Retry the real worker. Verify one gallery photo, one attachment and one photo-upload message; the same version ID is returned. The API accepts the matching durable Stored/SHA-256 receipt and transitions to Acknowledged.
6. Verify retention still deletes nothing during the configured **two-second test grace**. After expiry, the authenticated retention endpoint deletes exactly the site's acknowledged original. A private pull returns 404, WorkDay gallery metadata remains Acknowledged, and Studio's original still reads back with the same SHA-256.

The DNG is a structural transport fixture, not a camera sensor-decoding claim. This test covers the connected upload/import/ACK/retention journey; it does not claim full device behavior or run the worker's account-provisioning and snapshot-publication background loop. Its clients, server and databases are all local/disposable. Credentials, enrollment codes and password hashes are not logged or committed.

## Additional passing coverage

- PublicSite: **71 passed**, none skipped; `npm audit` reports zero vulnerabilities. Includes Google authentication, booking, watermark, JPEG gallery, shoot-completion and RAW transfer regression tests.
- RAW API coverage includes 250 MiB transfer, reordered/idempotent chunks, bad chunk/full checksums, interruption/restart, crash after original commit/before queue insertion, malformed RAW, unauthorized account/assignment/subject, quota/concurrency, cancellation, preview failure, verified ACK, Conflict/replay safety and positive retention grace.
- Existing local Chrome acceptance passed worker hashing, interruption/reselection resume without resending confirmed chunks, RAW/JPEG selection, progress, gallery reload, server cancellation, pause/removal and sign-out during hashing. WorkDay DOM interaction tests passed. No competing renderer was introduced.
- Existing RAW Python verification: **14 passed**. Real upstream [NEF](https://github.com/letmaik/rawpy/blob/main/test/iss030e122639.NEF) and [CR2](https://github.com/letmaik/rawpy/blob/main/test/RAW_CANON_40D_SRAW_V103.CR2) samples were also accepted and rendered with PR #101's canonical decoder, producing bounded JPEG previews. Samples stay outside Git.
- Reviewed the full diff and canonical owners; `git diff --check` passes.

## Separate UI test debt

The 451-test result is the complete Studio **backend/.NET** suite. Existing WorkDay web DOM tests pass. Broader unrelated Jobs/CSS UI checks still contain retired stylesheet paths, obsolete layout assumptions and an existing row-style ownership conflict; those application/UI files are unchanged in this focused WorkDay repair. They are not counted as passing or as device/tooling limitations.

## Device and tooling limitations

These are separate from the resolved backend/Android build blockers:

- Android APK/AAB build and dependency audit pass; physical-device/emulator install, native picker, offline/background/kill/restart, and existing-device database upgrade acceptance have not been performed. A build is not a device run.
- iOS build/signing/device checks remain unavailable: the Mac has Command Line Tools rather than full Xcode and lacks the iOS workload. Android can now be selected independently using `WorkDayBuildTarget` without restoring iOS.
- Docker is unavailable on this Mac. The repository-root Docker packaging still needs an image build before container deployment.
- Real camera decoding was exercised for NEF/CR2. The other requested camera families have structural container coverage; model-specific CR3/NRW/ARW/RAF/ORF/RW2/DNG and HEIC codec samples remain camera acceptance work. Unsupported previews preserve originals.
- Production HTTPS, proxy limits, disk/quota headroom, service deployment and live Studio↔AWS behavior are deliberately untested in this local-only task. Production remains unchanged; follow the rollout/rollback guide and obtain deployment authorization separately.

## Reproduction

From the repository root, with `STUDIO_TEST_POSTGRES` pointing only to disposable local PostgreSQL and `CommonRoot` pointing to the existing sibling Common checkout:

```sh
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -m:1 -p:CommonRoot=/path/to/Common
dotnet build Aegis.Studio.Web/Aegis.Studio.Web.csproj -c Release -m:1 -p:CommonRoot=/path/to/Common
```

Connected test alone (requires Node 24 and `npm ci` in PublicSite):

```sh
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -m:1 -p:CommonRoot=/path/to/Common --filter FullyQualifiedName~WorkDayConnectedUploadTests
```

Android, with MAUI Android installed, JDK 21 and Android SDK 36 available:

```sh
dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:WorkDayBuildTarget=net10.0-android -f net10.0-android -r android-arm64 -c Release -p:JavaSdkDirectory=/path/to/jdk21 -p:AndroidSdkDirectory=/path/to/android-sdk
dotnet package list --project Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj --no-restore --framework net10.0-android --vulnerable --include-transitive --format json
```

This run used the temporary toolchains under `/private/tmp/aegis386-dotnet`, `/private/tmp/aegis386-jdk21` and `/private/tmp/aegis386-android-sdk`; the system SDK was not changed. Android output is under `Aegis.Studio.WorkDay/bin/Release/net10.0-android/android-arm64/` and is not committed.

PublicSite:

```sh
cd Aegis.Studio.PublicSite
npm ci
npm audit
npm test
```

Local logs/TRX evidence: `/private/tmp/aegis-387-studio-final.log`, `/private/tmp/aegis-387-results/studio-final.trx`, `/private/tmp/aegis-387-android.log`, `/private/tmp/aegis-387-android-audit.json`, `/private/tmp/aegis-387-release.log`, and `/private/tmp/aegis-387-public.log`. No secrets, production database dumps, camera originals or signed binaries are committed. See [storage, rollout and rollback instructions](WORKDAY-RAW-INGESTION.md).
