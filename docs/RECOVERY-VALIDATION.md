# Integrated workstream recovery — 2026-10-05

Recovered the existing checkout on `studio-full-demo-ui` at local checkpoint `9f279864081c3bf8839c0b1102c3265936bc7e6c`. Its working tree was clean. The older `studio_acceptance` checkout and its uncommitted changes were left untouched. Published main was `cc881bc97040e2122d816a122f6b106ce8d7bea3`; the integrated checkpoint had not been published there. Local earlier commits are reconstruction snapshots, so the recovery branch uses published main as its parent and preserves the recovered source changes, excluding whitespace-only snapshot differences.

## Evidence and changes

The recovered implementation contains job-scoped QC aggregate/item queries, independent sale-line production IDs and quantities, immutable approved source links, Front Desk partial balance payments through POS, common signed Finance events and filters/exports, and a retained browser refund operation ID. Existing SQL integration assertions cover 100,000 metadata assets, disjoint 100-image pages, 50,000-image classification, approved-v3 bytes remaining pinned after v4, POS refund replay, real-photo derivatives, demo upgrade replay and scheduling conflicts. These are code/test findings, not a fresh PostgreSQL execution result.

The upload semaphore limited active ingestion to two, but did not cap waiting requests. Recovery adds admission capacity for eight waiting requests; excess requests receive the existing retryable 429 response before reading their multipart bodies. Cancellation, timeout and disposal release capacity. Focused tests exercise saturation, queued admission, cancellation, timeout and repeated disposal.

The former combined PostgreSQL workflow test is split into independent schemas for gallery scale/selections, approved sources/multi-line orders/refund replay, derivatives/original retention, demo upgrade replay, and WorkDay rescheduling/conflict rollback. The order test now checks two purchased lines using the same product and approved photo, and verifies that refunding one line preserves the other. A further scenario verifies partial balance-payment completion and refund retries against shared Finance records. Existing assertions remain retained.

The public-source test-photo fixture has embedded EXIF, XMP and Photoshop metadata removed; its decoded pixels remain identical to the recovered original.

## Validation in this recovery environment

- UI: 32 tests passed using the standard Node test suite.
- Recovered Web assembly: 163 tests passed, six database tests skipped, using an in-process reflection runner because the standard test host cannot create its communication sockets here. Domain: three tests passed.
- Current Web and Web.Tests source compile directly with the recovered .NET 10 compiler, available framework references and preserved dependency assemblies. Nullable/compilation warnings are errors; the package/framework version-unification diagnostic normally suppressed by MSBuild is suppressed for this direct check. This does not replace solution restore/build, project targets, analyzer checks or the normal xUnit host.
- Current recompiled Web tests: 165 passed, 11 database scenarios skipped with the same in-process runner. No database scenario is claimed green in this recovery run.
- Whitespace check passed. No new corrective CSS, duplicate business store, or replacement of existing workflow code was introduced.

The earlier checkpoint's report of a successful Release build and 172 .NET tests (including PostgreSQL) is retained in `INTEGRATED-WORKSTREAM.md` as historical evidence. Its original test logs were not available. The recovered SDK/test outputs and isolated PostgreSQL files were present, but the NuGet package cache was absent. A fresh solution build failed due to missing packages. The standard test host cannot create named-pipe sockets, and this environment cannot switch to the unprivileged identity required to start PostgreSQL. Direct compilation and in-process checks were the available alternatives.

## Remaining gate before main

Do not merge this recovery branch until a normal restored Release solution build and all PostgreSQL tests pass. Use a disposable test database with permission to create/drop schemas:

```powershell
$env:STUDIO_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Database=studio_test;Username=studio_test;Password=<test-password>'
dotnet restore Aegis.Studio.slnx
dotnet build Aegis.Studio.slnx -c Release --no-restore
dotnet test Aegis.Studio.slnx -c Release --no-build
npm --prefix tests/ui ci
npm --prefix tests/ui test
```

Expected current count is 179 .NET cases (176 Web including 11 PostgreSQL scenarios, and three Domain), plus 32 UI tests. Follow with the connected Load Demo Data walkthrough and local provider/role validation described in `INTEGRATED-WORKSTREAM.md`. The 100,000-asset fixture measures metadata operations, not 100,000-photo decode throughput. Main remains unchanged until these gates are satisfied.
