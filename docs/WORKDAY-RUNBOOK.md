# Aegis Studio WorkDay Runbook

## Purpose

Aegis Studio WorkDay is the Android/iOS field execution client for Aegis.Studio. Studio remains authoritative. WorkDay is an offline-capable client, not a second Studio installation or database.

## Architecture

WorkDay -> HTTPS API Site at https://api.bryanstudiosltd.com/api/v1 -> private TLS Studio upstream -> existing Studio services.

The mobile app has no Studio host setting and sends every WorkDay request to the API Site. The API Site proxies only the allowlisted WorkDay authentication, field, messaging, notification and assignment-gallery routes. It forwards authentication cookies and Studio-issued device tokens to Studio; Studio remains authoritative and keeps all existing authorization, scheduling, messaging, storage and audit rules. The proxy does not create a second WorkDay database or queue. The app's encrypted SQLite cache and outbox remain the offline record.

On the device:
- MAUI presentation
- protected SQLite operational cache
- durable operation outbox
- durable upload queue
- durable location-evidence queue
- secure device-session token storage

Server-side WorkDay data lives in the authoritative Studio PostgreSQL database.

## Authentication and device sessions

LOGIN authenticates the user through the API Site; the API Site forwards the request to Studio over validated HTTPS. The credential is not saved by the API Site. Session cookies and Studio-issued device tokens pass through the gateway to Studio.
SIGN IN FOR WORK starts a WorkDay work session. They are different events.

After normal Studio authentication, WorkDay requests a device session. The raw device token is returned once and stored in platform SecureStorage. Studio stores only a SHA-256 token hash. Device sessions are revocable and expire according to WorkDay:DeviceSessionDays.

A lost/stolen device:
1. revoke the affected WorkDay device session;
2. do not rotate Studio-wide credentials merely because one device was lost;
3. review the user's WorkDay audit/work-session activity;
4. remove local app data through device management where available;
5. issue a new device session after the replacement device authenticates normally.

## Local data

The local database is app-private SQLite. Client/session display data, offline note text and precise location coordinates are AES-GCM protected with a random key held in platform SecureStorage. Queue/index fields remain queryable so WorkDay can recover synchronization state.

The local schema is versioned. The pre-release plaintext queue/location layout is migrated in place: sensitive values are protected first, then legacy plaintext columns are scrubbed. Unsynchronized operations and uploads are never removed by age-based assignment retention.

## Synchronization

Every locally generated operation has a stable OperationId.

States:
Pending -> Sending -> Acknowledged
                  -> Failed -> retry
                  -> Conflict -> intervention

The server treats OperationId as the idempotency key. Version-sensitive assignment changes include ExpectedVersion. Ambiguous version conflicts are retained as Conflict rather than silently overwriting server state.

WorkDay must never show a local operation as synchronized until Studio acknowledges it. The mobile transport always targets `https://api.bryanstudiosltd.com/api/v1`; requests are relayed to Studio and retain Studio's existing idempotency and authorization behavior.

## Upload queue

A file selected in WorkDay is first copied into app-private storage and assigned a stable UploadId.

States:
Pending -> Uploading -> Uploaded
                   -> Retrying / Failed

Studio stores WorkDay files through the existing AttachmentStore/Common.Storage path. The server-side source key is workday:<UploadId>, preventing retry from creating uncontrolled duplicate attachments.

"Uploaded" means Studio acknowledged persistent storage. Starting an HTTP upload is not sufficient.

## Offline behavior

If Studio is unreachable:
- cached assignments remain available;
- field actions are stored locally;
- notes/exceptions remain locally durable;
- selected files remain in the upload queue;
- location observations remain queued;
- the UI reports OFFLINE - LOCAL WORK SAFE;
- synchronization state remains Waiting/Unknown rather than Healthy.

After a prior successful login, a still-valid revocable device session permits WorkDay to reopen offline. No user password is stored.

## Work Session and location policy

Modes:
- Disabled
- SignInOutOnly
- AssignmentEvents
- BackgroundDuringWork

The default is SignInOutOnly.

Location evidence is optional evidence, not proof of employee conduct. Failure to obtain GPS does not invalidate an otherwise legitimate Sign In/Out unless organizational policy is separately changed to require it.

BackgroundDuringWork begins only while a Work Session is active and stops on Sign Out. The current client uses battery-conscious interval sampling rather than continuous high-accuracy GPS. When the operating system suspends the application, WorkDay explicitly reports the sampling state as OS-suspended/continuity-unknown rather than pretending evidence collection remained continuous.

General Operations telemetry NEVER contains precise coordinates.

## Privacy and authorization

Location history is sensitive. The server timeline endpoint requires Administrator or Studio.WorkDay.Location.View. Each location-history view is written to a dedicated access-audit table.

Attendance corrections require Administrator or Studio.WorkDay.Attendance.Correct. A correction writes an immutable correction record containing the original values, corrected values, reason, actor and timestamp before the current session row is changed.

Precise location retention is controlled by WorkDay:LocationRetentionDays (default 30). Work-session/attendance records are retained independently from raw location observations.

General WorkDay fleet health may expose:
- client count
- recently online count
- stale/offline count
- pending synchronization
- pending/failed uploads
- work-session active state
- location permission/service state

It must not expose precise employee coordinates.

Workspace messaging remains the default external sending identity. "Reply as Myself" is offered only when the existing Studio SendAsSelf permission and approved Studio email identity both permit it. Studio's existing outbound audit records the actual user, identity type and From address.

## Expected normal state

A healthy online client should show:
- Connectivity: ONLINE
- synchronization: Healthy
- pending operations: 0
- failed uploads: 0
- Last Sync: recent
- location state consistent with configured policy

A safely offline client is degraded, not failed:
- Connectivity: OFFLINE
- Local Store: Healthy
- Synchronization: Waiting
- pending counts may be non-zero

No telemetry is Unknown/Stale, never Healthy.

## Diagnostics and Operations

WorkDay publishes safe client observations to Studio. Studio aggregates them into an Aegis.Operations diagnostic named Aegis.Studio:WorkDayFleet.

WorkDay flow definitions:
- WorkDaySession: Assigned -> Accepted -> Arrived -> InProgress -> Completed -> Synchronized
- WorkDaySync: LocalChange -> Pending -> Sending -> ServerProcessing -> Acknowledged
- WorkDayUpload: Selected -> LocalQueue -> Upload -> Storage -> MetadataAssociation -> Acknowledged
- WorkDayMessage: WorkDay -> Studio -> CommonMessaging -> Provider -> Recipient

Runbook reference: docs/WORKDAY-RUNBOOK.md

## Common failures

### Studio unavailable
Continue local work. Do not clear queues. Restore connectivity and synchronize.

### Authentication expired/revoked
Keep local pending work. Require normal authentication to obtain a new device session. Then resume synchronization.

### Upload interrupted
The device copy remains queued. Retry with the same UploadId.

### Version conflict
Do not silently overwrite. Keep the operation Conflict and resolve against the authoritative Studio state.

### Location unavailable
Record the operational action without location evidence where policy allows. Diagnostics should state location unavailable/denied rather than inventing coordinates.

### Device storage failure
Stop accepting new field actions that cannot be durably persisted. Tell the user their new work is not yet safe.

## Deployment

Server:
1. deploy the Public Site gateway and updated Aegis.Studio.Web;
2. configure `/etc/aegis/public-site.env` with `WORKDAY_STUDIO_ORIGIN=https://studio.bryanstudiosltd.com`, `WORKDAY_STUDIO_CONNECT_ADDRESS=127.0.0.1`, and `WORKDAY_STUDIO_TIMEOUT_MS=120000`; the API Site connects to the local Nginx listener using Studio's HTTPS certificate;
3. ensure the API Nginx upload limit permits WorkDay files (at least 32 MB; increase it if Studio accepts larger uploads);
4. ensure PostgreSQL migrations/schema initialization completes;
5. configure WorkDay policy keys, including LocationTrackingMode, DeviceSessionDays, TelemetryStaleSeconds and LocationRetentionDays;
6. ensure Aegis.Operations URL/registration identity is valid;
7. verify the API Site gateway by signing into WorkDay and completing an online bootstrap/sync.

Mobile:
1. WorkDay is locked to `https://api.bryanstudiosltd.com/api/v1`; it does not connect directly to the Studio host;
2. authenticate normally and allow WorkDay to issue a device session;
3. grant location permission only if organizational policy uses location evidence;
4. verify an online sync before field use.


No database, OpenBao, SMTP, Slack, Teams, Microsoft Graph or storage-provider secret belongs on the device.

## Version upgrade

Do not clear SQLite/app-private files during upgrade unless explicitly part of a tested migration. Pending operations/uploads must survive application updates.

## Logs

Server technical logs follow Aegis.Studio hosting/logging configuration.
Mobile user-facing state should avoid sensitive client details in diagnostic logging.
Precise GPS coordinates are not emitted to general diagnostics.

## Acceptance - offline field failure

1. authenticate online and synchronize today's assignments;
2. disconnect network;
3. open an assignment;
4. mark Arrived;
5. Start Session;
6. add notes and an exception;
7. add files;
8. Complete Session;
9. terminate WorkDay;
10. restart while still offline;
11. verify assignments, pending operations and queued files remain;
12. restore connectivity;
13. synchronize;
14. verify each operation is acknowledged once;
15. verify uploads complete once;
16. verify Studio reflects the final session state;
17. verify pending counts return to zero;
18. verify Operations/diagnostics show successful synchronization.

## Acceptance - field day / attendance

1. LOGIN;
2. SIGN IN FOR WORK;
3. capture start location if available;
4. apply configured tracking policy;
5. travel to assignment;
6. mark ARRIVED and START SESSION;
7. lose Internet and continue work;
8. retain all events/location evidence locally;
9. complete assignment;
10. restore connectivity and synchronize once;
11. SIGN OUT;
12. stop background tracking;
13. management user with Studio.WorkDay.Location.View can inspect the authorized timeline.

PASS requires no silent loss, no duplicate synchronization, no continued routine tracking after Sign Out and no unauthorized location-history access.

## Escalation

Escalate when:
- local durable storage cannot write;
- repeated synchronization conflicts cannot be resolved deterministically;
- upload source files are missing before acknowledgement;
- device tokens cannot be revoked;
- precise location appears in general Operations telemetry;
- unauthorized users can access WorkDay location history or sending identities.


## Administrator correction

Use the authorized WorkDay management screen or POST /api/workday/management/sessions/{workSessionId}/corrections with a reason and corrected timestamps. Never edit attendance rows directly. Correction history is append-only and preserves the prior values and correcting user.

## Registration decision

WorkDay mobile devices do not pretend to be independently deployed server applications in Common.Registration. They use revocable Studio-issued device sessions bound to a WorkDay device ID. Aegis.Studio itself remains the registered Aegis application and publishes aggregate WorkDay operational evidence to Operations.
