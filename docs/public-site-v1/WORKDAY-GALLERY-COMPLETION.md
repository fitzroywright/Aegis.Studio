# WorkDay web, remote media and request-bound gallery exchange

Scope: implementation items 1–6. Item 7 (email reconciliation, build/test proving,
merge and deployment) is explicitly excluded. This is a source checkpoint, not
evidence of runtime acceptance or production enablement.

## Ownership

Studio remains authoritative for staff, assignments, permissions, jobs, sessions,
collections, subjects, approved media and release decisions. Studio initiates all
API communication. Staff actions and audit records commit locally before external
communication; workers synchronize queued work in the background.

The reference website is an independent HTTP consumer. The dedicated reference-server.mjs host exposes the client-gallery UI without initializing any API database or upload store; it may run on its own process/host with independent volumes. The main server retains Booking and WorkDay web access. Its client identity/gallery
database and image directory are separate from API storage. After import it serves
its own images without needing Studio or the API to be online. Booking remains the
existing catalog, availability and intake flow, including BK/BE references.

## WorkDay web and mobile

The /workday entry provides staff enrollment/sign-in, authorized jobs/sessions,
collection/subject selection, multi-file batches, progress, pause/resume, unfinished
upload cancellation and My Uploads. Destinations include current sessions and
eligible assigned sessions from the last 60 days for post-shoot intake. Cancelled
assignments and closed jobs are excluded. Mobile caches authorized destinations and
queues its selected files while offline.

Web and mobile use the same upload protocol. All new exchange IDs use lowercase UUIDs. Each file has an immutable upload ID,
destination, name, size and SHA-256 checksum. Chunks are at most 4,000,000 bytes;
the server persists the confirmed offset and verifies each chunk. Changed bytes or
destinations cannot reuse an identity. Browser resume requires selecting the same
files/batch and destination; file contents and credentials are not kept in localStorage.

JPEG, PNG, WebP and TIFF are supported, with matching signatures and extensions.
The maximum is Studio's existing 100 MB per-file limit (the API may lower it),
with up to 1,000 files per web batch. Malware scanning must succeed before import;
absent/failed scanning leaves files quarantined. Temporary storage has quotas.

| State | Meaning |
| --- | --- |
| Uploading | Transfer/integrity validation is incomplete. |
| Quarantined | Bytes are staged; file safety clearance is outstanding. |
| ReceivedByApi | Complete and scanned; Studio has not confirmed import. |
| Imported | Studio confirmed a durable media asset and immutable version. |
| NeedsReview | Destination/permission/media association requires staff attention. |
| Purged | Imported successfully; temporary API bytes were cleaned up. |
| Cancelled / Expired | This unfinished upload identity cannot be resumed. |

Studio rechecks permissions and destinations, independently verifies size/checksum,
and imports through existing MediaUploadReceipts, MediaProductionStore and
StudioVersionStore. New media is not converted to conversation attachments.
Interrupted acknowledgments recover existing stored receipts without another asset.
Duplicate bytes in a different collection are held rather than silently misplaced.
Photograph replacement follows existing immutable Studio version rules.

Job media's Remote uploads dialog lists results. Coordinators/production staff can
record a correction and queue an idempotent retry of the original destination.
A destination change requires a new declaration. Unfinished/quarantined files expire
after seven days; cancelled files are cleaned up on the next retention pass.
Imported files are eligible for API cleanup after 24 hours. Received/unimported and
held files remain until durable import or staff resolution. Mobile retains its local
copy until Studio acknowledges import.

## Production access

The existing WorkDay enrollment design is extended: Studio provisions active
photographers, who set a separate WorkDay password through a privately delivered,
one-time expiring code. AD passwords are not copied. Passwords use salted scrypt;
session tokens are stored as hashes. Web sessions last eight hours; mobile device
sessions last 30 days. Sign-out/device revocation invalidates the relevant session.

Studio reconciles the complete active staff list, deactivating removed accounts and
revoking their sessions. Authorization leases and snapshots expire after the offline
window (default 24 hours, configurable from 1–24). After expiry, remote staff access
waits for fresh synchronization; internal Studio work remains independent. Browser
mutations use the existing CSRF mechanism and same-origin checks. Production requires
HTTPS, Secure cookies and NODE_ENV=production. The authentication boundary remains
replaceable without changing job/media exchange.

## Client Gallery journey

The /gallery-requests reference entry demonstrates separate website-owned client
accounts and eight-hour sessions. Website sign-in identifies a requester; it does
not verify entitlement. External developers may use another sign-in provider.

The website saves a request locally and submits it through its server credential.
Studio imports it into Front Desk's Client Gallery Requests dialog, also accessible
from Job media. Staff match an existing job/client, verify entitlement through a
trusted contact channel and record a verification reference. Held/rejected requests
expose no files. Reasons sent to clients must be client-facing; private verification
references remain in Studio. A released request cannot move to another client/job.

An authorized publisher selects an existing approved finished publication. Studio
pins its approved asset/version IDs, names, types, sizes and checksums in a transfer.
Gallery ID equals request ID. The API derives the destination integration from the
verified request; a supplied Client ID never authorizes file retrieval.

The website polls its scoped events, downloads and resumes files with Range,
verifies checksums, activates the entire version atomically in its own database,
and acknowledges the exact transfer/version/digest. Receipt is distinct from import.
Acknowledgments are stored for exact replay. Manifest digests use SHA-256 over UTF-8
JSON with recursively sorted object keys and preserved array order.

Updates select another approved publication and the next version. Stable item IDs
identify additions, replacements and removals. All new files must be complete before
activation; superseded website copies are removed before acknowledgment. Stale events
cannot roll newer state backward.

## Revocation and activity

Studio saves the withdrawal reason and queues it locally. Changing a released request
to Held/Rejected also queues withdrawal. The API blocks retrieval once the instruction
arrives. The website separately reports receipt, completion or failure; stops serving
the gallery; removes active/staged files; and reports deletion limitations. Completion
is the website's attestation, not a guarantee about client downloads or operator backups.
Late import reports cannot reverse revocation. Each withdrawal names throughVersion, so a delayed withdrawal cannot remove a later explicitly approved publication. Version counters are monotonic; cancelled local drafts may leave gaps. Re-publication requires another verified
approval, explicit release-after-withdrawal confirmation and a new publication version.

Studio distinguishes pending synchronization, API receipt, external receipt, completion
and failure. Unanswered withdrawals remain visible and become overdue (default 60
minutes). API transfer copies become eligible for cleanup 24 hours after external
completion; superseded copies wait for acknowledgment of the newer version, and revoked
copies wait for confirmed withdrawal. Activity metadata survives image cleanup.

Studio actions/outbox records and audit evidence share PostgreSQL transactions. The
API keeps append-only SQLite activity; the website records imports/withdrawals. Studio
deduplicates external events and preserves occurrence and receipt timestamps separately.
The review dialog includes activity filters, attempts, last/next attempt, safe errors
and retry. Logs correlate request, operation, upload and transfer IDs; they exclude
tokens, credentials, email bodies and file contents.

## Configuration contract — not applied in this pass

| Component | Setting | Purpose |
| --- | --- | --- |
| Studio | WorkDay__ApiSyncKey | Dedicated workday:sync credential. |
| Studio | PublicSite__BaseUrl | API HTTPS origin. |
| Studio | PublicSite__IntegrationToken | Existing Studio credential including gallery:publish. |
| Studio | PublicSite__GalleryExchangeEnabled=true | Enable request-bound exchange; suppress legacy automatic gallery pushing. |
| Studio | PublicSite__GalleryRevocationOverdueMinutes | Default 60; permitted 5–10,080 minutes. |
| API | MALWARE_SCANNER_PATH | Real scanner executable; missing/failed scans quarantine files. |
| API | REMOTE_MEDIA_PATH | Durable upload staging directory. |
| API | REMOTE_MEDIA_MAX_FILE_BYTES | Maximum 100,000,000; may be lowered. |
| API | REMOTE_MEDIA_USER_QUOTA_BYTES / REMOTE_MEDIA_TOTAL_QUOTA_BYTES | Defaults: 10 GB per user / 100 GB total. |
| API | WORKDAY_AUTHORIZATION_MAX_AGE_HOURS | Default 24; permitted 1–24 hours. |
| API | GALLERY_TRANSFER_QUOTA_BYTES | Default 100 GB staged gallery bytes. |
| Reference website | REFERENCE_GALLERY_API_URL | API HTTPS origin; localhost HTTP only outside production. |
| Reference website | REFERENCE_GALLERY_API_KEY | Dedicated gallery:request and gallery:transfer credential only. |
| Reference website | REFERENCE_GALLERY_DB_PATH / REFERENCE_GALLERY_MEDIA_PATH | Independent durable website storage. |

Credentials remain server-side. Preserve integration IDs when rotating secrets to
retain request ownership. The authoritative contract is
Aegis.Studio.PublicSite/public-site-v1.openapi.json, served at /api/v1/openapi.json.
No real credentials, production settings, account changes, builds/tests, merges or
deployments are applied by this source checkpoint.
