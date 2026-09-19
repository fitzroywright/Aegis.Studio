# Aegis Studio WorkDay — Final Implementation Report

Date: 2026-09-19

## WORKDAY BUILD STATUS

Architecture: PASS (source/static review)
Home / My WorkDay: PASS (source/static review)
Assignments: PASS (source/static review)
Session Workflow: PASS (source/static review)
Customer Details: PASS (source/static review)
Navigation: PASS (source/static review)
Notes: PASS (source/static review)
Exceptions: PASS (source/static review)
Offline Store: PASS (source/static review)
Synchronization: PASS (source/static review)
Conflict Handling: PASS (source/static review)
Uploads: PASS (source/static review)
Messaging: PASS (source/static review)
Reply as Myself: PASS (source/static review; reuses existing Studio authorization/audit)
Galleries: PASS (source/static review; view/status only unless existing authorization permits server actions)
Notifications: PASS (source/static review)
Security: PASS (static review; independent penetration/runtime proof pending)
Audit: PASS (source/static review)
Diagnostics: PASS (source/static review)
Operations Telemetry: PASS (source/static review)
Runbook: PASS
Automated Tests: PASS (tests written; execution proof pending after final hardening commits)

## Architecture delivered

- Aegis.Studio remains the sole authoritative Studio system.
- Aegis.Studio.WorkDay is an Android/iOS MAUI field client.
- WorkDay never connects directly to PostgreSQL.
- WorkDay does not receive OpenBao/provider/database credentials.
- Revocable Studio-issued device sessions are used for mobile continuity.
- The raw device token is stored in SecureStorage; the server stores a hash.
- Aegis.Studio.WorkDay.Core contains platform-neutral durability/state rules and is included in the normal Studio solution/test graph.

## Field workflow delivered

- login distinct from Work Sign In;
- Work Sign In / Sign Out;
- My WorkDay home;
- Today / Upcoming / Needs Attention;
- local authorized assignment search;
- assignment/customer/instruction detail;
- navigation/contact handoff;
- Accept/Confirm;
- Arrived;
- Start Session;
- internal notes;
- structured exceptions;
- exception status;
- file queue/upload;
- customer/workspace messaging;
- Workspace sender default;
- permission-gated Reply as Myself;
- gallery status;
- Complete Session.

## Offline and synchronization delivered

- durable SQLite local database;
- schema versioning;
- non-destructive pre-release migration;
- protected cached customer/session information;
- protected queued note text;
- protected precise location coordinates;
- stable operation IDs;
- durable outbox;
- retry/restart recovery;
- idempotent server replay;
- explicit Conflict state;
- server acknowledgement before synchronized/uploaded state;
- assignment retention that never ages out pending work;
- offline startup after prior valid device authentication;
- no false green when Studio is unreachable.

## Uploads delivered

- selected file copied into app-private storage first;
- stable UploadId;
- Pending / Uploading / Uploaded / Failed / Retrying;
- retry with same UploadId;
- Common.Storage-backed Studio AttachmentStore on server;
- server acknowledgement required before Uploaded;
- local source retained on transport failure;
- no automatic offline door/control-style side effects exist in WorkDay.

## Work Session and field-presence evidence

- explicit Work Session model;
- original offline timestamps preserved;
- one normal active Work Session per user;
- concurrent/cross-device Sign In serialized per user;
- Sign In / Sign Out location evidence when available;
- Disabled / SignInOutOnly / AssignmentEvents / BackgroundDuringWork policy model;
- battery-conscious sampling while the mobile process can collect evidence;
- OS suspension exposed as continuity unknown, never falsely represented as continuous evidence;
- offline location evidence queued idempotently;
- management timeline and map handoff;
- precise location excluded from general diagnostics/Operations;
- location-history access audited;
- configurable precise-location retention;
- administrator attendance correction with immutable before/after/reason/actor history;
- GPS represented as field-presence evidence, never absolute proof of conduct.

## Diagnostics and Operations

WorkDay publishes timestamped safe client observations including:

- connectivity;
- synchronization state;
- pending operations;
- oldest pending operation;
- pending uploads;
- failed uploads;
- active Work Session;
- location permission/service state.

Aegis.Studio aggregates WorkDay clients and publishes WorkDay fleet diagnostics to Aegis.Operations.

No observation = Unknown, never Healthy.

Flows implemented:

- WorkDaySession: Assigned -> Accepted -> Arrived -> InProgress -> Completed -> Synchronized
- WorkDaySync: LocalChange -> Pending -> Sending -> ServerProcessing -> Acknowledged
- WorkDayUpload: Selected -> LocalQueue -> Upload -> Storage -> MetadataAssociation -> Acknowledged
- WorkDayMessage: WorkDay -> Studio -> Common.Messaging/provider path

Runbook: docs/WORKDAY-RUNBOOK.md

## Security review

Static review found and addressed:

- no WorkDay database credentials;
- no OpenBao credentials;
- device token binding/revocation;
- assignment mutation ownership validation;
- permission-gated sensitive management/location routes;
- permission-gated Reply as Myself through existing Studio policy;
- precise GPS absent from general operational telemetry;
- offline note/GPS values protected at rest;
- legacy plaintext queue/location columns migrated and scrubbed;
- location-history access audited;
- correction history append-only;
- local application backup disabled on Android.

## Existing Studio functionality reused

WorkDay reuses rather than duplicates:

- Studio users/photographer schedule-resource mapping;
- Studio schedule/booking/job data;
- Workspace/customer conversations;
- existing sending identity resolver/audit;
- Common.Messaging server-side infrastructure;
- Common.Storage via Studio AttachmentStore;
- Studio gallery API;
- Studio notifications;
- Common.Security/Studio authorization patterns;
- Studio Operations flow publishing.

## Tests added / relevant tests

WorkDay-specific:

- deliberate assignment/session transition rules;
- exception-state normalization;
- WorkDay flow ordering;
- Unknown-vs-acknowledged synchronization semantics;
- offline/local-safe connectivity semantics;
- pending operation rules;
- upload retry/safety semantics;
- assignment retention with pending work;
- conflict behavior;
- background-location policy state;
- stale telemetry rules.

Existing Studio tests continue to cover:

- Workspace safe sender default;
- authorized Reply as Myself;
- unauthorized Reply as Myself;
- allowed sender-domain behavior;
- permission delegation restrictions;
- notification branch independence;
- sensitive route authorization;
- Common.Security integration.

## Files/projects created

- Aegis.Studio.WorkDay/
- Aegis.Studio.WorkDay.Core/
- Aegis.Studio.Web/WorkDay/
- Aegis.Studio.Web.Tests/WorkDaySemanticsTests.cs
- Aegis.Studio.Web.Tests/WorkDayClientRulesTests.cs
- WORKDAY_IMPLEMENTATION_PROGRESS.md
- docs/WORKDAY-RUNBOOK.md
- scripts/Build-WorkDay.ps1

## Major existing files/projects modified

- Aegis.Studio.Web/Program.cs
- Aegis.Studio.Web/Production/ProductionEndpoints.cs
- Aegis.Studio.Web/StudioOperationsFlowPublisher.cs
- Aegis.Studio.Web/OperationsTelemetryPublisher.cs
- Aegis.Studio.Web/appsettings.json
- configuration/Aegis.Studio.configuration-contract.json
- Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
- Aegis.Studio.slnx

## Remaining failures

No known source-level implementation TODO is intentionally left in the WorkDay directive scope.

Independent proof is still required for:

1. final Release server build/test after the last hardening commits;
2. Android MAUI restore/build/package;
3. iOS MAUI build/package on macOS;
4. physical-device offline/restart/reconnect acceptance;
5. physical-device OS suspension/location permission/battery behavior;
6. two-location validation using real Studio assignments/data.

## Exact independent build commands

From the Aegis.Studio repository on the normal development machine:

    .\scripts\Build-WorkDay.ps1 -AndroidOnly

For server/tests only:

    .\scripts\Build-WorkDay.ps1 -SkipMobile

On macOS with the required .NET MAUI/iOS workload and Xcode:

    dotnet workload restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj
    dotnet restore Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -p:TargetFramework=net10.0-ios
    dotnet build Aegis.Studio.WorkDay/Aegis.Studio.WorkDay.csproj -f net10.0-ios -c Release --no-restore

## Acceptance execution

After compile proof, execute the two documented procedures in docs/WORKDAY-RUNBOOK.md:

- Offline Field Failure
- Field Day / Attendance

Then validate Main Studio, second Studio location and off-site assignment data without changing the single-server architecture.

## Handoff

IMPLEMENTATION COMPLETE
TESTS WRITTEN
STATIC REVIEW COMPLETE
DOCUMENTATION COMPLETE
RUNBOOK COMPLETE
LOCAL BUILD PROOF PENDING

READY FOR LOCAL COMPILE PROOF: YES


## Photographer client consolidation

The former Aegis.Studio.Photographer MAUI client has been retired. Its functional scope is now part of Aegis.Studio.WorkDay:

- signed-in field profile/display name;
- photographer-resource filtered schedule;
- booking/job time, location, status and notes;
- availability/unavailability/leave view and entry;
- quick “tomorrow unavailable” action;
- schedule navigation/contact actions;
- assignment confirmation and field lifecycle;
- offline schedule cache and WorkDay outbox;
- messaging, gallery status, uploads, notifications and diagnostics.

The legacy /api/mobile/photographer route surface has been retired. WorkDay uses /api/workday as the mobile field API. The server-side PhotographerStore/profile and availability data model remain because WorkDay and Studio scheduling use them; only the obsolete standalone client/API surface was removed.


## Cross-workspace photographer access model

Photographers are global Studio field resources, not workspace-bound users.

Assignment rule:

- a photographer may be assigned to a job owned by any workspace;
- assignment of the photographer's schedule resource automatically ensures JobStaff membership on that specific job;
- WorkDay visibility requires both assignment to the photographer resource and job-level ViewJob access;
- WorkDay field-state changes require job-level ManageProduction;
- WorkDay file uploads require job-level AddAttachments;
- WorkDay gallery status is exposed through an assignment-scoped endpoint and does not grant general workspace gallery access;
- availability/leave remains photographer-global and is not workspace-scoped.

Automatic assignment reconciliation is intentionally non-destructive: it will not overwrite an existing explicit job membership and will not remove job membership when a schedule assignment changes, because membership provenance is not yet modeled.


## Workspace managers, reassignment, capacity and Job Flow

Workspace management now includes a non-global `WorkspacePermissionSet.Manager` preset. A workspace manager can view and operate the workspace, create/edit jobs, manage scheduling/production, notify and manage ordinary workspace members, and use approved Studio sending identities, while `AdminWorkspace` remains false.

Managers can monitor only photographer activity relevant to their workspace through:

- `GET /api/workday/workspaces/{workspaceId}/activity`
- `/workspaces/{workspaceId}/workday-management`

The manager view shows assigned photographers, recent WorkDay connectivity/synchronization evidence, active work-session state, assignments in that workspace, and current capacity.

Photographer capacity is calculated from:

- configured nominal daily capacity (`Scheduling:DefaultDailyCapacityHours`, default 8);
- scheduled assignment minutes;
- declared Unavailable/Leave minutes.

Capacity states are `Available`, `NearCapacity`, `OverCapacity`, and `Unavailable`.

Jobs/schedule assignments are explicitly reassignable through:

- `POST /api/schedule/entries/{id}/reassign`

Reassignment:

- enforces `ManageSchedule`;
- checks schedule conflicts;
- stores immutable assignment-change history;
- grants JobStaff access to newly assigned photographers;
- does not destructively remove old explicit job membership because membership provenance is not yet modeled.

Photographers are global Studio resources and can be assigned into any workspace. Assignment grants job-scoped access rather than standing workspace membership.

Assigned JobStaff now has `ReplyRequester` for that job. Customer replies therefore work for cross-workspace photographers through the existing conversation API, whose safe default sender remains the owning workspace email identity. `SendAsSelf` remains separately workspace-controlled.

A first-class Operations flow named `JobFlow` is published for tracking-light visualization:

`Created -> Assigned -> Accepted -> Arrived -> InProgress -> Completed -> Synchronized`

The same job ID is used as the correlation/business identifier across schedule assignment, reassignment, WorkDay field actions and Studio job lifecycle updates, allowing Aegis.Operations to show where a job is currently sitting or stalled.


## Overtime and weekend work tracking

WorkDay now classifies completed and active Work Sessions into auditable time evidence:

- total worked minutes;
- regular minutes;
- overtime minutes;
- weekend minutes;
- weekend-overtime minutes (the intersection of weekend and overtime evidence).

Classification is based on raw Work Session sign-in/sign-out timestamps. The raw timestamps remain authoritative and are not replaced by the derived totals.

Policy is configurable:

- `WorkDay:TimeTracking:TimeZoneId` — default `America/Jamaica`;
- `WorkDay:TimeTracking:RegularDailyHours` — default `8`;
- `WorkDay:TimeTracking:WeekendDays` — default Saturday/Sunday.

Multiple Work Sessions on the same local day share one regular-time threshold. Sessions crossing local midnight are split across local dates before classification.

Weekend work is tracked independently from overtime. A weekend minute is not automatically declared overtime merely because it occurred on a weekend. If weekend work also exceeds the configured daily regular threshold, that overlapping portion appears as `WeekendOvertimeMinutes`.

This is time evidence, not payroll calculation. Studio does not apply overtime multipliers, statutory rates or payroll amounts. Those remain the responsibility of Payroll/rules processing.

Photographers can view their own recent totals through:

- `GET /api/workday/time-summary`
- the WorkDay mobile home screen (14-day summary).

Workspace managers see the same 14-day regular/overtime/weekend totals for photographers assigned to their workspace through the workspace WorkDay management dashboard.
