# WorkDay API exchange

WorkDay Mobile connects only to `https://api.bryanstudiosltd.com/api/v1/`. Studio never exposes an inbound WorkDay API for the Public Site to call. Studio's hosted exchange worker makes outbound HTTPS requests to the API Site, publishes account snapshots, and polls for pending mobile submissions.

## Data flow

- Studio provisions WorkDay accounts from active photographer profiles using the `workday:sync` integration credential.
- Studio publishes each photographer's current assignments, availability, time summary, messages, notifications, and reply sender options to the API Site.
- WorkDay Mobile reads its account snapshot from the API Site and submits field reports, availability changes, uploads, and messages there.
- The API Site persists submissions in its SQLite exchange database. Studio polls the queue, applies each update locally, and posts an acknowledgement or conflict result back to the API Site.
- The mobile app keeps its local assignment cache, field-operation outbox, availability queue, upload files, and encrypted message/thread cache. Messages and client replies composed offline are saved in an encrypted local outbox with stable operation IDs, then synchronized automatically when connectivity returns. Uploaded files remain on the device until Studio acknowledges storage. Offline reads use cached assignments and message threads where available; uncached API-only views require connectivity.

## Exchange routes

Mobile routes are under `/api/v1`: authentication and enrollment, `/workday/bootstrap`, `/workday/operations`, `/workday/availability`, `/workday/assignments/{id}/uploads`, `/messages/threads`, `/conversations/{id}/messages`, `/notifications`, and `/workday/assignments/{id}/gallery-status`.

Studio uses the integration credential for:

- `POST /admin/workday/users` to provision an account.
- `PUT /admin/workday/snapshots` to publish the current snapshot.
- `GET /admin/workday/updates?kind=...` to pull queued submissions.
- `POST /admin/workday/updates/{operationId}/result` to acknowledge or reject a submission.

The API Site stores passwords as scrypt hashes and session credentials as token hashes. Mobile sessions are scoped to the provisioned WorkDay account. The exchange database is durable and uses idempotent operation IDs so Studio can safely retry after connection failures.

## Studio configuration

Set the server-side integration key in `/etc/aegis/studio.env`:

```dotenv
WorkDay__ApiSyncKey=<integration key with workday:sync scope>
```

The enrollment file defaults to `/var/lib/aegis/studio/workday-enrollment-codes.json`, inside Studio's persistent, service-owned state directory. The worker creates the file with owner-only permissions. Protect that file as a secret. Deliver each one-time enrollment code through the studio's approved private channel; never put codes in source control or logs.

After changing the environment:

```sh
sudo systemctl restart aegis-studio
sudo systemctl is-active aegis-studio
sudo journalctl -u aegis-studio -n 100 --no-pager
```

Confirm that the exchange worker is running, a provisioned account receives its one-time code, first-time enrollment succeeds, snapshots appear in WorkDay Mobile, and an operation is acknowledged after Studio polls it. Test uploads and conversation replies with non-production data. Builds and automated tests should run on the developer Mac before deployment.

## Job gallery and upload message

JPEG, PNG and WebP uploads from the public WorkDay site are imported through Studio's existing media upload receipts into the assignment's Job photo gallery. The corresponding photography session is resolved from the schedule/shoot/workflow-session relationship; normal required-session and crew validation still apply. Assignment ownership and AddAttachments remain checked by both WorkDay upload entry points before importing.

Each completed upload creates one internal Job conversation message:

> A photo was uploaded to this Job: portrait.jpg
> Uploaded by photographer.one via WorkDay. View it in the Job photo gallery.

The immutable version, gallery asset, internal attachment and message use stable receipt identities. Retry resumes incomplete completion and does not add another photo or message. The API acknowledgment now includes optional jobId, assetId, versionId and messageId fields while retaining the existing attachmentId contract for mobile clients. Studio acknowledges only after all these records are saved. Non-photo native uploads remain attachments, with a file-upload message. Nothing is automatically approved or published to clients.

### Recover earlier attachment-only uploads

Deploy and restart Studio with this change first, after local Mac validation. On the AWS API host, identify the existing WorkDay SQLite database from the protected runtime configuration without displaying secrets. Run the recovery tool for the exact uploading username, initially without --apply:

~~~bash
python3 tools/repair-workday-gallery.py --database /actual/path/intake.sqlite --user demo.jason
~~~

The preview displays upload IDs and filenames only. It selects that user's old Acknowledged image uploads that have attachment receipts but no gallery version/message receipt. Review the selected rows, then repeat with --apply. The tool takes a consistent, owner-only SQLite backup and requeues the existing operation rows; original payloads, upload IDs and previous results remain intact. It does not create new users, reset passwords, or upload photos again.

Studio rechecks the assignment and Job permissions, reuses the existing matching attachment, and creates the gallery asset and Job message. Refresh the Job gallery/conversation and the WorkDay upload gallery. Verify new acknowledgments contain assetId, versionId and messageId. Items in Conflict require explicit review; the tool does not bypass permission, session, quarantine or closed-Job restrictions. Repeat bounded recovery only after checking the first batch.

### Local verification

Use the Mac with .NET 10, the repository's sibling Common dependencies, and a disposable PostgreSQL database in STUDIO_TEST_POSTGRES:

~~~bash
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --filter FullyQualifiedName~WorkDayPhotoImportTests
python3 tools/test_repair_workday_gallery.py
~~~

Then exercise the deployed test account: upload a photo, confirm it in the Job gallery, check the internal filename/uploader message, retry the same upload, and verify there is one photo and one message. Run a preview and controlled legacy recovery before any bulk repair. These are required acceptance steps, not claims that tests have already run.
