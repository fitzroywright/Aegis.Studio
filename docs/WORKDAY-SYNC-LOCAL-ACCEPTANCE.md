# WorkDay synchronization — local acceptance

Implementation branch: `feature/studio-complete-workflow-20261011`. Update Studio and the mobile client together. No build or automated test execution was performed for this checkpoint.

## Behavior

WorkDay mobile acceptance uses `https://api.bryanstudiosltd.com/api/v1` as its only network base URL. The Public Site gateway forwards the existing operations to Studio's internal `/api/workday` routes. Studio-native acceptance checks may call the internal Studio routes directly; do not configure the mobile app with the Studio URL.

WorkDay retains assignment progress, session/collection/roster photography status and operational notes in its encrypted local outbox. Home and assignment details provide Sync Now, progress, outstanding changes, last fully successful synchronization and failure/conflict feedback. The successful timestamp survives restart and does not advance while conflicts remain.

With WorkDay active, internet reconnection starts sync automatically. Active retries occur every 30 seconds, including when Wi-Fi is available but Studio was unavailable. Returning to WorkDay starts synchronization after mobile suspension. No guarantee is made that a killed/suspended iOS or Android application executes background tasks.

Assigned session/collection/roster targets cache during bootstrap. Select SESSION / COLLECTION / ROSTER STATUS from assignment details. Field progress is distinct from overall booking cancellation, final production approval and operational Job closure. Cancelled, Deferred and Absent require a reason in the UI. Studio receives notes and auditable reports; Job Owner and Workspace Manager receive existing workflow notifications and can see Assignments & Workflow > Photographer reports.

## Local checks

```sh
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release
pwsh ./scripts/Build-WorkDay.ps1
```

Set `STUDIO_TEST_POSTGRES` to a disposable database before executing PostgreSQL cases. Android/iOS builds require their supported platform tools; provisioning and installation are separate checks.

1. Assign a photographer, add a collection and roster subjects, then sync WorkDay online. Verify cached names and targets.
2. Disconnect internet. Queue assignment events, a roster Completed/Deferred status and operational notes. Verify pending count and local descriptions, then restart WorkDay while offline; reports must remain.
3. Restore internet with WorkDay open. Verify automatic progress, acknowledgements, refreshed statuses, Owner/Manager notifications and Studio report history. Do not press Sync Now for this check.
4. Repeat with WorkDay suspended. Reopen it and verify automatic synchronization. Then verify Sync Now manually.
5. Interrupt a request after Studio commits but before the response reaches the device. Retry using automatic and manual sync. Verify one operation/note/target revision and one notification for each audience.
6. While offline reports are pending, change the corresponding roster in Studio or have another assigned photographer submit a newer field status. Reconnect: WorkDay must show Conflict, retain original facts and avoid overwriting the newer status. Review reports/resolve conflict must preserve history before a newly reviewed operation is queued.
7. Make Studio unreachable despite working Wi-Fi. Failed reports stay queued and active retries resume delivery when Studio returns. Invalid/revoked credentials must not silently use another login.
8. Verify last successful synchronization survives restart and remains unchanged on failures/conflicts. Confirm no updates are discarded by logout while unacknowledged.

Record platform, commit, results and screenshots in local acceptance notes. Current source implementation is not device or connected-environment acceptance.

## Photographer journey extension — 11 October 2026

WorkDay remains the assigned photographer's mobile client. Bootstrap caches the Job title, client contact allowed by the existing service, session time/location/instructions, collections and roster names, references, collection names and photo notes. Roster people are Job subjects; they are never sign-in accounts. Crew and ownership decisions use Studio employee account links and active photographer profiles. Pickers search the local Studio employee service with bounded results; scheduling and WorkDay administration no longer preload the messaging/AD user list. Studio-maintained employee activation and names are not reset by picker reads.

### Contracts and conflict rules

| Contract | Meaning |
| --- | --- |
| `GET /api/workday/bootstrap` → `fieldTargets[assignmentId]` | Existing authorized bootstrap, additive data only. Target fields: `targetId`, `scope` (`Session`, `Collection`, `Roster`), `name`, `sourceFingerprint`, `version`, `status`, optional `collection`, `reference`, `instructions`. Follow-up assignments expose only their selected roster remainder. |
| `POST /api/workday/operations` → `RecordFieldStatus` | Existing operation envelope: stable `operationId`, assignment, actual occurrence time, device, reviewed reporter `expectedVersion`, text, and JSON `category` containing `targetId`, `status`, `sourceFingerprint`, `expectedTargetVersion`, optional reason. Accept the assignment before recording fields. |
| Photography status | `Photographed`, `Absent`, `Deferred`, `Cancelled`, `NotStarted`, `InProgress`. Older `Completed` and `NoShow` values remain readable and map to Photographed/Absent in roster counts. Exceptions require a reason. These statuses do not express Finance authority or production approval. |
| Reporter versus target revision | Acceptance/arrival/start/completion use the existing per-photographer assignment revision. Roster and collection targets have a shared Job revision across all assignments and devices; session targets are scoped to their assignment. Both reviewed revisions are retained in queued operations. |
| Source fingerprint | Detects newer Studio roster facts, instructions and subject photo links. It is a change detector, not authentication or authorization. Studio still checks the active photographer, current assignment, mutable Job and accepted assignment. |
| Acknowledgement/replay | Studio records original operation facts and atomically applies a valid field report, notes, supervisor notifications and audit. Identical replay returns the earlier receipt; changed actor/facts with the same ID are rejected. Failed transport keeps the original encrypted SQLite operation for retry. |
| Conflict | Studio returns HTTP 409, retains rejected field facts in `studio_workday_field_conflicts`, and notifies Owner and Manager. The device retains the original report and stops dependent reports for that assignment. Replaying a recorded conflict retains that outcome. Refresh and review with the Job Owner, record a recovery reason, then submit a new operation against the reviewed facts. No automatic rebase or last-write-wins. Conflict history remains visible after review. |
| Counts | Job Details and Media use the same unpaginated real-subject progress projection; system “Unclassified” subjects do not inflate roster totals. A field observation applies only while its reviewed Studio facts still match. Newer Studio edits take precedence, and earlier WorkDay evidence remains in history. Collection/session observations do not bulk overwrite child roster statuses. |
| Completion | Field status records photography observations. Use the existing Arrived → Start Session → Complete Session controls for actual attendance and workflow advancement. Shared shoot completion waits for every current assigned photographer. Remaining absent/deferred/not photographed/retake/blocked subjects block source submission and remain visible for follow-up scheduling. |
| Remainder | Existing connected workflow command `reschedule-remainder`, with normal operation ID/Job revision, required new session, crew/time/location/reason and `subjectIds`. Server rechecks each outstanding subject, uses the existing scheduling locks and availability checks, preserves the old shoot and creates a scoped follow-up. Replays do not create additional sessions. Source review cannot be silently reopened. |
| Uploads | Job Details file/folder drops and subject drops declare batches through the existing upload service and send stable `uploadId`s. Retry original batch after lost responses. Stored versions/history survive failed uploads. Multiple-session media drops ask for the session; subject drops direct the user to Media Upload for that selection. Existing Production Add/Upload supports reopening interrupted server batches and reselecting original files. |

Local roster caches, notes, field JSON (including reasons), and queue contents use the existing device data protector. The additive local migration encrypts previously stored category values. Studio account binding prevents one user's pending reports being sent under another user's enrollment. Automatic reconnect synchronization runs while the app is active; reopening/resuming retries durable work. OS-suspended/terminated background delivery is not claimed.

### Focused acceptance cases and evidence

| ID | Case | Required observation | Evidence status |
| --- | --- | --- | --- |
| J1 | Studio assign owner; assigned owner accepts; assign photographer and schedule | Another user cannot accept ownership. Crew cannot be scheduled before acceptance. Active photographer sees matching Job/time/location/collections/roster/instructions in WorkDay. | Server journey/UI test source added; device run pending. |
| J2 | Photographer accepts, disconnects, records one Photographed, one Absent, one Deferred and an operational note | Reports visibly pending; offline restart retains exact IDs, revisions, text and roster. Decline requires a reason and remains separately auditable. | Manual physical-device acceptance pending. |
| J3 | Restore Internet; then repeat with Sync Now | Live progress, acknowledgements, last successful sync and correct counts: 1 Photographed, 1 Absent, 1 Deferred; roster total 3. Owner/Manager see reports and notifications. | Automatic sync source implemented; actual reconnect/device acceptance pending. |
| J4 | Lose response after Studio commits; retry original operation/upload | One report/note/audit outcome and one stored file receipt/version, no duplicate records. | PostgreSQL replay test and Job Details upload retry UI test source added; unrun. |
| J5 | Edit roster/photo links in Studio after device caches, or report same subject from another assignment/device | Clear conflict, original queued report retained, no overwrite; Owner/Manager notified, conflict history visible. Refresh/reason/new decision required. | PostgreSQL conflict test source added; concurrent-device field run pending. |
| J6 | Complete attendance, review counts and reschedule remaining subjects | Existing session retains actual time and reports. New session contains only selected remainder, repeats preserve one follow-up, completed/cancelled subjects rejected. Source submission stays blocked until outstanding photography is resolved. | `WorkDayRosterJourneyTests` added; unrun. |
| J7 | Open Job Details workflow phase and Front Desk Next action / Flow | Assign owner → Accept ownership → Assign photographer → Schedule session follow permissions/state and use the same existing commands. No accepted ownership control for another account. | Focused UI test source added; role/browser run pending. |
| J8 | Search person picker; drop files/folders on media or a subject | Only Studio employees/active photographer profiles offered; roster subjects never become assignees. No messaging/AD directory preload. Drop creates receipts and failed original batch can be retried. | Source review/static parsing complete; browser/storage run pending. |
| J9 | Reassign/cancel/close or change account while offline; reconnect | No unauthorized write or account mixing. Original reports remain available for review; lost committed receipts remain replayable. | Existing recovery boundaries reused; local/device run pending. |

Automated source files: `Aegis.Studio.Web.Tests/WorkDayFieldSyncTests.cs`, `WorkDayRosterJourneyTests.cs`, `tests/ui/job-details.test.cjs`, plus existing manual photographer/upload receipt and WorkDay client rule suites. **No builds or automated tests were run for this extension.** JavaScript syntax checks and `git diff --check` only. Record commit, device/OS, user identities, Job/session IDs, outage timing, operation/upload IDs, counts, notification/audit evidence and pass/fail for each case during local validation. Do not treat added test source as passed evidence.
