# Studio and WorkDay acceptance handoff

Branch: `studio_acceptance`, based on Studio main `6698de083b7dc2f03b4f1aaee089b7981f8381f5`.

This is implementation for local acceptance, not a claim of tested production readiness. No restore, .NET build, test execution, application run, mobile build, or deployment was performed remotely. Changed JavaScript files received syntax checks and the patch received whitespace checks. Visual layout still needs your browser/device review.

## Implemented changes

- Compact dark left navigation: My Work, Workspaces, Pipeline, Production, FrontDesk, Dashboard, Admin. The workspace folder list remains available.
- A real Job workspace with Messages, Media, Subjects, Tasks, Calendar and Activity. The default Messages section shows the conversation alongside Media. What's Next exposes real tasks and allowed Job actions; Details is collapsible.
- Conversation Files contains message-linked files. Conversation attachments stay separate from Media. Failed attachment upload/linking can be retried without posting the message again.
- CSV/XLSX roster file import uses the existing subject importer. The first XLSX sheet is read as cell values; formulas and spreadsheet errors are rejected. Blank rows are ignored, IDs retain leading zeros when stored as text, and duplicate/missing IDs or names produce validation errors. Imports remain atomic.
- Website intake uses the same pending request/deposit ledger as FrontDesk. A stable external reference prevents concurrent duplicate requests; conflicting reuse is rejected. Deposits create exactly one Job, link/create its client and carry the requested slot into the scheduling table.
- Assigning a shoot reuses the requested schedule entry. Repeated assignment of the same photographer/time returns the existing shoot. Existing resource conflict checks remain authoritative.
- Studio checkout and native WorkDay Start/Complete use the same shoot records. When all active shoots are complete, the Job advances to Awaiting Upload and records activity and an internal message.
- Explicit Job actions: submit review, request changes, resubmit, approve for production, finish production, and send to gallery provider. They check state, media and unfinished tasks; operation receipts protect retries. Approval creates a QC task. Finishing production records the final approved media versions.
- Approved media is closed for upload at Ready for Client / Published. Request changes before revising a ready Job, then repeat review and production. The gallery manifest references the approved versions.
- Gallery publication has a provider-neutral contract. The `Manifest` implementation writes a real handoff manifest through Common.Storage to the configured storage target. It does not claim an external client site is live.
- Lifecycle notifications use the existing JobEventNotifier/Common.Messaging route. Notification enqueue failures are logged after the business transaction commits.
- Native .NET MAUI WorkDay retains Android/iOS targets and its offline queue. Shared earth/gold styling is applied to its pages and navigation.
- Demo seed is Development-only. Existing real authentication, Development-only Mock Authentication, Common.Storage S3 configuration, Common.Messaging queues and control-plane integration remain in place. Gallery provider configuration is included in standard diagnostics.

## Required configuration

Use your existing Common sibling checkout and existing secrets. Configure these values locally; no credentials are included in this branch.

| Setting | Value/purpose |
|---|---|
| `ConnectionStrings__Studio` | Studio PostgreSQL connection, or its existing Common.Secrets resolution |
| `ASPNETCORE_ENVIRONMENT` | `Development` for local mock/demo; `Production` for production |
| `MockAuthentication__Enabled` | `true` for local testing only; production never enables this path |
| `MockAuthentication__UserName` / `DisplayName` / `Groups__0` | Select local personas; non-admin personas need normal workspace/job memberships and an active photographer profile to use WorkDay |
| `BookingApi__RequiredDeposit` | Your approved positive JMD deposit amount; default `0` deliberately disables website intake until configured |
| `ClientGallery__Provider` | `Manifest` to enable the implemented gallery handoff; blank disables publication |
| `Storage__Provider` | `S3` for Bryans/TrueNAS; configure Development too when proving actual storage |
| `Storage__S3__Endpoint` / `Bucket` / `Region` | Your TrueNAS S3 endpoint, bucket and signing region |
| `Storage__S3__ForcePathStyle` | `true` for the existing on-prem configuration |
| `Storage__S3__AccessKeyIdSecretName` / `SecretAccessKeySecretName` | Existing Common.Secrets names for the S3 credentials |

See `docs/STORAGE-TARGETS.md` for complete storage settings and `docs/WORKDAY-RUNBOOK.md` for WorkDay device sessions and offline sync. Existing Common.Messaging, Common.Secrets and Aegis control-plane settings still apply.

To exercise different mock roles, select a distinct mock user, configure a non-administrator group where appropriate, and grant that user the intended permissions through Admin. The current mock implementation defaults to the configured administrator groups when its group list is empty. Production ignores the Mock Authentication flag.

## Database migration

There is no EF migration command. Studio applies idempotent schema initialization at startup. This change adds an approved-version reference on media assets, an external-booking reference index on pending requests, and a Job action receipt table. Start with a database backup and a disposable acceptance database; run Studio using the command below to apply initialization. Existing data remains in its existing tables.

## Pull, restore, build, test and run locally

PowerShell, with Common at `D:\Projects\Common`:

```powershell
Set-Location D:\Projects\Aegis.Studio
git fetch origin
git switch --track origin/studio_acceptance

dotnet restore .\Aegis.Studio.slnx
dotnet build .\Aegis.Studio.slnx -c Release --no-restore
dotnet test .\Aegis.Studio.Domain.Tests\Aegis.Studio.Domain.Tests.csproj -c Release --no-build
dotnet test .\Aegis.Studio.Web.Tests\Aegis.Studio.Web.Tests.csproj -c Release --no-build

# Optional transactional tests; use a disposable PostgreSQL database.
$env:STUDIO_TEST_POSTGRES = '<disposable PostgreSQL connection string>'
dotnet test .\Aegis.Studio.Web.Tests\Aegis.Studio.Web.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~FrontDeskBookingRequestTests|FullyQualifiedName~MediaProductionPersistenceTests'
Remove-Item Env:STUDIO_TEST_POSTGRES

Push-Location .\tests\ui
npm ci
npm test
Pop-Location

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:MockAuthentication__Enabled = 'true'
$env:ClientGallery__Provider = 'Manifest'
# Set ConnectionStrings__Studio and the real approved BookingApi__RequiredDeposit locally.
dotnet run --project .\Aegis.Studio.Web\Aegis.Studio.Web.csproj -c Release --no-build --no-launch-profile -- --urls http://localhost:5080
```

If the branch already exists locally, use `git switch studio_acceptance` and `git pull --ff-only origin studio_acceptance` instead of `git switch --track`.

Infrastructure tests skip without their environment settings. Check the existing integration test documentation for additional opt-in Common.Security configuration.

WorkDay Android (on your Windows machine with Android SDK/MAUI installed):

```powershell
dotnet workload restore .\Aegis.Studio.WorkDay\Aegis.Studio.WorkDay.csproj
dotnet restore .\Aegis.Studio.WorkDay\Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-android
dotnet build .\Aegis.Studio.WorkDay\Aegis.Studio.WorkDay.csproj -f net10.0-android -c Release --no-restore
dotnet build .\Aegis.Studio.WorkDay\Aegis.Studio.WorkDay.csproj -f net10.0-android -c Debug -t:Run
```

WorkDay iOS (on your Mac with Xcode/MAUI installed, from the Studio checkout):

```bash
dotnet workload restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj
dotnet restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-ios
dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -f net10.0-ios -c Release --no-restore
dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -f net10.0-ios -c Debug -t:Run -p:RuntimeIdentifier=iossimulator-arm64
```

Use `iossimulator-x64` on an Intel Mac. Release device signing/distribution uses your existing Apple/Android configuration. The MAUI project remains outside the server solution, so server builds do not require mobile workloads.

## Website contract

The existing POST `/api/bookings` now creates a pending FrontDesk request instead of immediately creating a Job. Its response has `bookingRequestId`, `status`, `jobId` (initially null), `requiredDeposit` and `recordedDeposit`. The new explicit route is POST `/api/public/booking-requests`. Both use the existing BookingApi authentication/signature mechanism and `CreateBookingIntakeRequest` fields, including required `externalBookingId`.

GET `/api/public/booking-requests/{id}` reports its pending/confirmed state. POST `/api/public/booking-requests/{id}/payment-confirmations` accepts `amount`, `currency`, `provider`, `reference` and uses the same authenticated boundary and verified deposit ledger. Configure the website server as an authorized BookingApi client; do not put its signing secret in browser code.

Legacy `/api/bookings/{id}` and payment/status routes remain for historical Studio booking records. New website callers should use the pending-request routes above. This is a deliberate contract change and the final public website is not part of this work.

The gallery manifest is stored under `studio/client-galleries/{jobId-without-hyphens}/publication.json`, beneath the configured target root. Its media references use Common.Storage keys. A client site/provider must authenticate and consume that manifest/media through its own boundary. No Pixieset or AWS assumption is introduced.

## Manual acceptance sequence

1. Sign in with Mock Authentication in Development. Seed demo data in Admin. Confirm all seven primary destinations and the Job layout at desktop and narrow widths.
2. Submit an authenticated website booking with a stable external reference. Replay it concurrently; confirm one pending request, no Job and a useful rejection if the same reference is used for different details.
3. In FrontDesk record a partial deposit, then the remaining required amount. Replay the receipt. Confirm one client-linked Job in Needs Scheduling, correct deposit totals and an internal confirmation message.
4. Open Job Calendar, check availability and assign an active photographer. Confirm the requested slot is reused. Confirm duplicate assignment reuses the shoot and a conflicting resource/time is rejected. Reschedule through Pipeline and verify Calendar/WorkDay show the same schedule.
5. On Android/iOS WorkDay accept, arrive, start and complete the shoot. Repeat with an offline completion followed by sync. Confirm the server updates that shoot and the Job becomes Awaiting Upload only after every active shoot is complete.
6. Upload actual images through configured TrueNAS/Common.Storage. Disconnect storage once and retry. Confirm failed uploads do not create a usable asset record and successful media has safe Unclassified defaults.
7. Import CSV and XLSX rosters, including a blank row, repeated ID and invalid row. Confirm good imports are immediately usable and invalid imports are atomic with validation messages. Assign/move images to subjects/collections; inspect versions and download an editing batch.
8. Submit for review. Request changes with a note. Upload returned versions, resubmit and approve for production. Confirm Activity/messages and the QC task. Attempt to skip stages and confirm rejection.
9. Complete production tasks and final QC, then Finish production. Confirm the Job is Ready for Client and further uploads require Request changes. Send to gallery provider; verify the actual manifest contains approved version IDs/keys. Replay the operation and confirm no duplicate activity/task.
10. Send workspace replies, internal notes and self replies using the relevant real sending identity configuration. Attach a file; fail upload/linking once and retry without another message. Confirm Conversation Files links to its source message and the file is not automatically promoted into Media.
11. Check task dates, Job Calendar, Activity, My Work and the Production queue. Verify permissions using staff/photographer and administrator personas, diagnostics, S3 availability and Common.Messaging delivery/failure logs.
12. Set Production and confirm Mock Authentication and demo seed cannot be used. Prove real authentication, TrueNAS, outbound messaging and the Aegis control plane on your deployment.

## Deliberate differences and remaining proof

- The workspace folder list stays beside primary navigation because it is part of the established Workspace → Job workflow.
- Subject navigation becomes a horizontal strip for small jobs and a searchable rail for larger jobs. Subject portraits are not invented when no portrait exists.
- Calendar uses the existing schedule and task records. Existing Pipeline scheduling views remain the rescheduling surface.
- The Job header uses available names/status/date rather than inventing reference numbers, tags or metadata that the current Job model does not have.
- Gallery delivery stops at the real provider handoff manifest. The external client-site consumer, public website and payment-provider truth must be proven locally/in their actual integrations.
- Existing raw-camera archive/backup verification is retained as a separate legacy archive workflow. The new media/review/publication path uses Common.Storage; this change does not redesign the camera archive or migrate those existing archive directories.
- Local builds, tests, visual review, role checks and infrastructure proof are outstanding. The branch must pass those before being called acceptance-ready. Existing Pipeline/FrontDesk/Admin features have been retained, not independently re-proven in this environment.
