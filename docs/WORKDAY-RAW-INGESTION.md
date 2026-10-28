# WorkDay camera originals

Implements #386 using the canonical RAW preview decoder from PR #101 (`Aegis.Studio.Web/Media/raw_preview.py`). Both public WorkDay and Studio call that helper in isolated subprocesses. Decoder children receive only allowlisted runtime environment variables, not application credentials. Studio continues to use its existing leased derivative worker. No second gallery renderer or client publication flow is introduced.

## Transfer and ownership

JPEG/PNG/WebP files at or below 25,000,000 bytes retain the existing multipart route and shoot-completion behavior. RAW (CR2/CR3/NEF/NRW/ARW/RAF/ORF/RW2/DNG), TIFF/HEIC/HEIF and larger images use resumable sessions. Default maximum is 512 MiB; configured limits may be lower. HEIC preview decoding depends on the installed libheif codec; preserving its original does not depend on preview availability.

Every session/chunk/status/completion/cancel/thumbnail request requires an active WorkDay account and a currently published assignment. Session identity binds uploader, assignment, filename, size, SHA-256 and published job/session context. Subject IDs are optional and must occur in that assignment's published `fieldTargets`; otherwise Studio's existing Unclassified association applies. Studio publishes its authoritative session mapping and resolves/validates the photography session and subject again during import. Assignment and Job permissions are checked again before Studio downloads anything.

Creation reserves quota in SQLite. Four MiB chunks are written to owner-only files, fsynced and renamed before durable chunk receipts are recorded. Reordered chunks are supported; a duplicate is accepted only for identical size/SHA-256. Completion streams chunks into a private original, verifies the whole-file SHA-256 and invokes the shared bounded container probe (one MiB metadata budget, directory/offset/dimension limits). No original is encoded into the SQLite queue or loaded into a whole-file buffer. A malformed or unsupported container never queues an import. Validation-runtime failures retain chunks for retry. Container validation does not promise a decoder supports every camera model.

WorkDay web hashes with an incremental worker, reports upload progress, and supports Pause/Retry. After reload or interrupted connectivity, reselect the identical file on the same assignment and retry; a stable content-bound identity retrieves received chunks. The browser does not persist the user's original file. Remove/Clear cancels an incomplete session; completed originals cannot be cancelled or silently removed. A cancelled identity cannot be reused. Use a replacement file (or a different filename) for a deliberately new upload.

Mobile copies a selected original into its offline queue with a bounded streaming size check. The Core transfer implementation hashes from disk, sends only missing chunks and checks the API's checksum receipt. Uploading records recover after app restart. Pause keeps the file; Retry/Resume continues the stable upload UUID. Cancel removes only never-attempted or explicitly server-cancelled incomplete transfers. A completed original stays on the device until Studio's matching Stored receipt confirms upload/assignment/size/SHA-256. Conflict, missing ACK and interruption retain the local original. Existing small-file multipart behavior is preserved.

## Studio pull, acknowledgment and retention

Only `workday:sync` integration credentials can GET `/api/v1/admin/workday/uploads/{id}/original`. Employee tokens and client-gallery sessions cannot download originals. Studio builds this URL itself; payloads cannot select a download host or storage path. It uses response-header streaming, checks size and SHA-256 headers, copies into a private temporary file, and recomputes SHA-256. Failed transfers receive no ACK and retry later.

Import reuses `WorkDayPhotoImport`, the media receipt, immutable version, internal attachment and internal Job message. Storage verifies its writes, and the ACK is emitted only after these records succeed. A failed partial import reuses the same stable identities. Preview failure does not roll back or delete an original. Required photography-session and crew checks still apply. A matching RAW receipt includes `versionId`, `attachmentId`, `uploadId`, `assignmentId`, `sizeBytes`, `sha256` and `storageStatus: Stored`.

The site refuses an ACK missing/mismatching those fields. Verified ACK and exchange result commit in one SQLite transaction. The original remains for `WORKDAY_ACK_GRACE_SECONDS` (seven days by default, minimum one hour in production). A 15-minute sweep, or the protected retention endpoint, deletes only acknowledged originals whose grace has elapsed. Metadata and generated thumbnails remain; unacknowledged and Conflict originals are never aged out automatically. Repeated ACKs do not extend or reset the deadline. Incomplete sessions consume reserved quota until explicitly cancelled; monitor and resolve abandoned sessions rather than deleting them blindly.

WorkDay thumbnails are private employee views. Client galleries still require explicit Studio approval/publication and generated, pixel-embedded watermarks. This change does not publish an original or bypass `PUBLIC_WATERMARK_PROCESSING_ENABLED` or private S3 configuration.

## API

Base `/api/v1` (OpenAPI in `Aegis.Studio.PublicSite/public-site-v1.openapi.json`):

- POST `/workday/assignments/{id}/upload-sessions`: `{uploadId,fileName,sizeBytes,sha256,subjectId?}`. Returns state, chunkBytes, receivedChunks, format and previewState. Same declaration is idempotent; conflicting identity is 409.
- GET `/workday/assignments/{id}/upload-sessions/{uploadId}`: durable resume status.
- PUT `.../{uploadId}/chunks/{index}`: `application/octet-stream`, `X-Chunk-SHA256`, exact declared chunk size (last chunk may be smaller). 200 receipt; bad checksum 422, exceeded size 413, concurrency saturation 503.
- POST `.../{uploadId}/complete`: verifies full original and queues the existing AssignmentUpload exchange descriptor. 202; missing chunks 409, checksum 422, invalid content 415, missing validator runtime 503.
- DELETE `.../{uploadId}`: only Receiving/Cancelled; 409 for completed originals.
- GET `/admin/workday/uploads/{uploadId}/original`: streaming octet-stream, Content-Length and X-Content-SHA256, private/no-store, `workday:sync` only.
- POST `/admin/workday/upload-retention`: eligible-original sweep, `workday:sync` only.

Gallery listing, thumbnail, polling, result and shoot-completion endpoints retain their existing paths. Large-upload descriptors use `largeUpload:true`, format/size/SHA-256 and context instead of `contentBase64`. Roll out Studio support before allowing new large uploads; old Studio workers cannot import these descriptors.

## Packaging and deployment limits (no automatic deployment)

Existing systemd hosts: deploy PublicSite plus the canonical `photo_probe.py` and `raw_preview.py`, preserving absolute private storage and SQLite paths across releases. Set `WORKDAY_PHOTO_PROBE_PATH` and `WORKDAY_RAW_PREVIEW_HELPER_PATH` to their packaged paths. Set `WORKDAY_RAW_PYTHON` to the isolated runtime installed from `raw-preview-requirements.txt`; Studio uses `Media__RawPreview__PythonExecutable`. Use a self-contained Python environment; decoder subprocesses do not inherit arbitrary PYTHONPATH/loader overrides or service secrets. A missing optional decoder gives Preview unavailable; a missing Python/container validator prevents completion and retains chunks. Keep these scripts owned by the release owner and unwritable by upload users.

Docker builds now use the repository root so the same canonical helpers are included:

```sh
docker build -f Aegis.Studio.PublicSite/Dockerfile -t aegis-publicsite:REVIEWED_COMMIT .
```

The image installs the pinned PR #101 runtime and runs Node as the node user. Keep `/app/data` persistent. Non-container releases need the same runtime/files. Do not copy a release over private upload storage.

New environment values are documented in `.env.example`. Max size is configurable up to 512 MiB (Studio's streamed import/download limit); quota defaults are 10 GiB globally and 4 GiB per account; processing concurrency defaults to two (maximum eight). Reserve disk for both chunks and assembly: allow at least twice reserved bytes plus previews, database/WAL and backup headroom on the site, and temporary/staged/authoritative copies on Studio. SQLite sessions and the private original directory form one logical backup: use SQLite online backup plus a filesystem snapshot or a controlled quiesce of transfer writes. Never copy an active SQLite database without its WAL consistency guarantee.

Nginx must retain `proxy_set_header Host $host`, both HTTPS hostnames, the existing multipart body limit (at least 26 MB), and allow four MiB chunk requests. Example for the existing application proxy location:

```nginx
client_max_body_size 26m;
client_body_timeout 180s;
proxy_send_timeout 180s;
proxy_read_timeout 180s;
proxy_request_buffering off;
```

Do not raise the proxy limit to 512 MiB: each resumable request is bounded. Ensure proxy temp storage is private if buffering is enabled elsewhere. HTTPS and service credentials are unchanged. No new server, bucket or IAM permission is needed: site originals use private persistent disk, and authoritative Studio storage retains the existing provider. This does not switch client-gallery S3 modes.

Monitor free disk/inodes, quota errors, 503 processing saturation, Receiving/Ready age, Conflict counts, preview failures, and ACK/purge backlog using counts/IDs (never credentials or file contents). Query the two `workday_upload_*` tables read-only for diagnostics. Investigate integrity errors before retrying; never manufacture an ACK to free space. A rollback must keep the database/private directory and upgraded Studio importer; prevent new resumable submissions until the new site is restored. Older site releases ignore the additive session tables but cannot finish or display new transfers.

## Local acceptance

Run `npm ci`, `npm audit`, `npm test`, the Studio tests with `STUDIO_TEST_POSTGRES` pointing only to a disposable PostgreSQL instance, the RAW preview/probe Python tests, and Android/iOS builds on the developer Mac. RAW upload tests cover actual 250 MiB transfer, reorder/duplicate, checksums, interruption/restart, authorization, failed preview, ACK/grace and cancellation. Studio tests cover streamed private download, bad hashes/truncation, resumable mobile transport and an actual 250 MiB byte-exact authoritative import. Browser acceptance uses real Chrome against a disposable local server, not production accounts. Device offline/background/restart behavior and real camera-model decoding require device/fixture acceptance before release; test reports must distinguish these from protocol simulations.
