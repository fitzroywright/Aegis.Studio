# Aegis Studio WorkDay Runbook

## Purpose

Aegis Studio WorkDay is the Android/iOS field execution client for Aegis.Studio. Studio remains authoritative. WorkDay is an offline-capable client, not a second Studio installation or database.

## Architecture

WorkDay -> authenticated Studio API -> Aegis.Studio -> existing Studio services.

On the device:
- MAUI presentation
- protected SQLite operational cache
- durable operation outbox
- durable upload queue
- durable location-evidence queue
- secure device-session token storage

Server-side WorkDay data lives in the authoritative Studio PostgreSQL database.

## Authentication and device sessions

LOGIN authenticates the user to Studio.
SIGN IN FOR WORK starts a WorkDay work session. They are different events.

After normal Studio authentication, WorkDay requests a device session. The raw device token is returned once and stored in platform SecureStorage. Studio stores only a SHA-256 token hash. Device sessions are revocable and expire according to WorkDay:DeviceSessionDays.

A lost/stolen device:
1. revoke the affected WorkDay device session;
2. do not rotate Studio-wide credentials merely because one device was lost;
3. review the user's WorkDay audit/work-session activity;
4. remove local app data through device management where available;
5. issue a new device session after the replacement device authenticates normally.

## Local data

The local database is app-private SQLite. Customer/session display data is AES-GCM protected with a random key held in platform SecureStorage. Queue/index fields remain queryable so WorkDay can recover synchronization state.

Unsynchronized operations and uploads are never removed by age-based assignment retention.

## Synchronization

Every locally generated operation has a stable OperationId.

States:
Pending -> Sending -> Acknowledged
                  -> Failed -> retry
                  -> Conflict -> intervention

The server treats OperationId as the idempotency key. Version-sensitive assignment changes include ExpectedVersion. Ambiguous version conflicts are retained as Conflict rather than silently overwriting server state.

WorkDay must never show a local operation as synchronized until Studio acknowledges it.

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

BackgroundDuringWork begins only while a Work Session is active and stops on Sign Out. The current client uses battery-conscious interval sampling rather than continuous high-accuracy GPS.

General Operations telemetry NEVER contains precise coordinates.

## Privacy and authorization

Location history is sensitive. The server timeline endpoint requires Administrator or Studio.WorkDay.Location.View.

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
1. deploy the updated Aegis.Studio.Web;
2. ensure PostgreSQL migrations/schema initialization completes;
3. configure WorkDay policy keys;
4. ensure Aegis.Operations URL/registration identity is valid;
5. verify /api/workday/bootstrap after authentication.

Mobile:
1. configure only the HTTPS Studio service URL;
2. authenticate normally;
3. allow WorkDay to issue a device session;
4. grant location permission only if organizational policy uses location evidence;
5. verify an online sync before field use.

No database, OpenBao, SMTP, Slack, Teams, Microsoft Graph or storage-provider secret belongs on the device.

## Version upgrade

Do not clear SQLite/app-private files during upgrade unless explicitly part of a tested migration. Pending operations/uploads must survive application updates.

## Logs

Server technical logs follow Aegis.Studio hosting/logging configuration.
Mobile user-facing state should avoid sensitive customer details in diagnostic logging.
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
