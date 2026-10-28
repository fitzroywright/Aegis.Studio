# Aegis Studio Public Site

Standalone Node 24 website for Booking and Client Galleries, with its own durable SQLite database and media copies. The public site does not call internal Studio. Studio polls the versioned public-site API every 60 seconds and pushes approved gallery media to the site.

Copy `.env.example` to `.env`, provision local test secrets, and run on the MacBook:

```bash
npm ci
npm test
npm start
```

The website accepts inquiries and bookings through its browser form and authenticated submission API. It saves before returning a receipt. Form and API share the intake store and annual location reference allocator (BK/BE bookings, W inquiries). Acknowledgment, import, booking and payment states remain distinct. Rosters can be entered, pasted or uploaded as CSV/XLSX, reviewed and saved. Attachments remain quarantined until scanning passes.

ExcelJS stays pinned to 4.4.0. A targeted npm override replaces its `uuid@^8.3.0` dependency with 11.1.1 to fix [GHSA-w5hq-g745-h8pq](https://github.com/advisories/GHSA-w5hq-g745-h8pq). ExcelJS uses only `require('uuid').v4()` for extended conditional-formatting IDs; UUID 11.1.1 preserves that CommonJS API and includes the advisory fix. The roster tests serialize real XLSX workbooks, exercise this ID generation, and read them through the production worker. Reassess the override when upgrading ExcelJS.

See [the API guide](../docs/public-site-v1/INTEGRATION.md), [OpenAPI](public-site-v1.openapi.json), and [deployment and live proof runbook](../docs/CLIENT-INTAKE-HANDOFF.md).

## Storage and hosting

Use a persistent writable volume for `INTAKE_DB_PATH`, including SQLite WAL files. The database stores intake, replay keys, roster drafts, attachment quarantine, payment events, gallery metadata, media, selection drafts and durable submitted selection snapshots. Back it up using SQLite's online backup mechanism or while the service is stopped. Do not copy only the main database file from an active WAL database. Restrict volume access and protect backups.

The Dockerfile uses Node 24 and runs as the unprivileged node user, listens on `0.0.0.0:8080`, and installs from the committed lockfile. Build from the repository root with `docker build -f Aegis.Studio.PublicSite/Dockerfile .` on the MacBook, then deploy the resulting image behind HTTPS with a persistent `/app/data` volume. Pin deployment image tags to the reviewed revision.

## Identity and payment boundaries

Gallery sign-in supports Microsoft, Facebook, Yahoo and Google when each provider's real credentials are configured. Mock sign-in is rejected. A verified provider subject must be linked to a Studio client UUID with a single-use code issued by Studio; matching an email address never grants gallery access. Each gallery request checks the binding.

### Google sign-in

Create an OAuth client of type **Web application** in Google Auth Platform, with an external audience for customer accounts. Configure branding, support contacts and the required website/privacy policy information. While the Google app is in Testing, add the accounts used for acceptance as test users. Configure the authorized redirect URI exactly as:

```text
https://web.bryanstudiosltd.com/auth/google/callback
```

Supply `GOOGLE_CLIENT_ID` and `GOOGLE_CLIENT_SECRET` through the protected runtime environment. Both are required before the existing provider renderer shows **Continue with Google**. No browser-side client secret or Gmail API access is used; the server requests only `openid profile email`. Google Workspace accounts are supported as well as Gmail accounts, subject to the customer's organization policies.

For the existing AWS systemd deployment, securely edit `/etc/bryans-reference-site.env`, set those two values, and restart `bryans-reference-site.service` after deploying this code. Verify the service and `/health`. Do not print secrets in logs, shell history or screenshots. The callback is on the web hostname, not the API hostname.

Before release, run on the MacBook with Node 24:

```bash
cd Aegis.Studio.PublicSite
node --test client-auth.test.mjs google-auth.test.mjs
npm test
```

Then perform a real Google sign-in, redeem a Studio-issued code for a test client, verify published gallery access, verify another Google account cannot see that client's galleries, and sign out. Unverified changes and mocked provider tests do not establish live provider acceptance. To disable Google sign-in, remove its runtime credentials and restart the service.

Google protocol reference: https://developers.google.com/identity/openid-connect/openid-connect

A verified payment-provider adapter can submit financial events with its dedicated `payment:verified` credential. Ordinary submitters cannot mark themselves paid. This site and Studio do not collect card details.

## Selection handoff and validation

Submitted selections are saved locally and await Studio import. Studio polls `GET /api/v1/gallery-selections` with `gallery:selections:pull`, commits the immutable snapshot and a staff-visible photograph list, then posts `/api/v1/gallery-selections/{reference}/ack` with `gallery:selections:ack`. Enable `PublicSite:SelectionPollingEnabled` in Studio. The site never connects to Studio.

See [deployment readiness](../docs/public-site-v1/DEPLOYMENT.md) for configuration, local verification and remaining blockers. Regression tests are local Mac checks; they do not establish production identity, live operational acceptance or AWS deployment readiness by themselves.

## WorkDay photograph uploads

Choose an assigned Job in the public WorkDay panel, then drop JPEG, PNG or WebP photographs into the drop zone or use **Choose photographs** to select several files. Each photograph may be up to 25 MB, with up to 100 files selected at once. Review thumbnails before choosing **Upload photographs**. Remove/Clear selection affects only files that have not been confirmed by the API.

The per-assignment gallery shows this signed-in photographer's own uploads and persists across page and service restarts through the existing WorkDay exchange database. **Refresh photographs** updates processing status; **Show older photographs** pages through history. Ready, Uploading, Queued for Studio, Acknowledged by Studio and Needs review are distinct states. Acknowledgment does not mean gallery approval or client publication.

Uploads are sent sequentially, with progress and independent failures. Retry retains each photograph's upload ID so a lost response cannot create a duplicate queue operation. Local file previews are released after confirmed upload or sign-out. A new sign-in cannot inherit another account's gallery or selected files.

Authenticated APIs:
- GET /api/v1/workday/assignments/{assignmentId}/uploads returns up to 40 metadata records and an optional before cursor.
- GET /api/v1/workday/assignments/{assignmentId}/uploads/{uploadId}/thumbnail serves a resized JPEG without original EXIF metadata.
- POST on the existing uploads collection continues to queue photographs for Studio.

Both read routes require the assignment in the current account's published schedule; thumbnail access also requires ownership of that upload. Responses use private/no-store caching. No public object URL or original download is exposed. The history reuses the existing exchange upload payloads; no additional original-image store or publication path is created. Thumbnail decoding is pixel-bounded and limited to two concurrent operations.

Run on the Mac with installed Node 24 dependencies before release:

~~~bash
(cd Aegis.Studio.PublicSite && node --test workday-gallery.test.mjs && npm test)
node --test tests/ui/workday-web.test.cjs
~~~

Then verify live with test accounts: drop and multi-select files, upload a mixed batch, retry a connection interruption, reload the gallery, switch assignments, refresh Studio acknowledgment, sign out, and confirm another photographer cannot view the first account's thumbnails. Verify keyboard controls and desktop/mobile layouts. These checks do not run automatically by adding this section.

Camera originals use private resumable storage and the shared PR #101 RAW decoder. See [WorkDay RAW ingestion](../docs/WORKDAY-RAW-INGESTION.md) before packaging or enabling large uploads.
