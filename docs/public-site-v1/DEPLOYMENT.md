# Reference Site deployment readiness

The standalone project is `Aegis.Studio.PublicSite` (`aegis-studio-public-site`), a Node 24 native HTTP service with plain browser assets. Studio initiates every integration request. The Reference Site never connects to Studio, including for WorkDay, photograph selections or media reads.

## Verify locally on the Mac

```sh
cd Aegis.Studio.PublicSite
npm ci
npm test
```

Run the Studio Web tests with .NET 10 and `STUDIO_TEST_POSTGRES` pointing only at a disposable local PostgreSQL database. `ReferenceGallerySyncTests` exercises failed uploads, lost responses, transactional review creation, revocation retry and selection acknowledgement replay. Run `npm test` from `tests/ui` for the UI suite. The full repository validation script is `scripts/Validate-ClientIntake.ps1`; it also restores and builds the solution. No cloud build is a substitute for Mac validation.

## Hosting requirements for AWS us-east-2

This repository supplies a Dockerfile, not an AWS deployment stack. Hosting and infrastructure remain an operations decision; no AWS resources are provisioned by these instructions. Build a revision-tagged image locally on the Mac for the chosen host's CPU architecture after review. The Dockerfile installs from the committed lockfile with `npm ci --omit=dev` and runs as the unprivileged `node` user.

Required runtime values:

```dotenv
NODE_ENV=production
HOST=0.0.0.0
PORT=8080
PUBLIC_DEMO_MODE=false
INTAKE_DB_PATH=/app/data/intake.sqlite
SESSION_SECRET=<unique random secret, at least 32 characters>
PUBLIC_API_PROVISIONING_TOKEN=<separate random secret, at least 32 characters>
PUBLIC_API_KEYS_JSON=[]
```

The container already sets `HOST=0.0.0.0`; direct local runs default to loopback. Container startup does not load `.env`; inject runtime values through the host's protected environment/secret store. `npm start` loads a local `.env` for development. Never configure a Studio upstream URL on the Reference Site.

Mount a persistent writable volume at `/app/data`, owned by the container's service user. SQLite stores gallery image BLOBs, attachments, credentials, intake, replay history and immutable selection submissions, using WAL and FULL synchronization. `WORKDAY_EXCHANGE_DB_PATH` optionally selects a separate persistent SQLite path; otherwise WorkDay shares `INTAKE_DB_PATH`. There is no S3 or PostgreSQL adapter in this service. Plan for a single application instance with local durable storage and SQLite-compatible locking; shared network storage and horizontal scaling have not been validated. Protect and test consistent online backups and restores; never copy only the active main database file while WAL is in use.

Configure DNS, HTTPS ingress, health checks (`GET /health`), body limits that permit 30 MB gallery uploads, storage capacity monitoring and backups. Browser assets use same-origin APIs; placing the browser site and API on separate origins requires additional work. Verify `GET /api/v1/openapi.json`. The current documented API origin is `https://api.bryanstudiosltd.com`.

Set `MALWARE_SCANNER_PATH` to a working clamscan-compatible executable if attachments must leave quarantine. The image does not install a scanner or signatures. Studio also requires `PublicSite__MalwareScannerPath` for attachment import.

## Provision credentials and enable Studio

Key provisioning uses the **`X-Site-Provisioning-Token` header**, whose value matches the site's `PUBLIC_API_PROVISIONING_TOKEN`. This bootstrap secret is not the ordinary Studio integration token and must never be sent to browser code or external developers.

- `POST /api/v1/admin/integration-keys` creates a named scoped credential and returns its secret once.
- `GET /api/v1/admin/integration-keys` lists metadata.
- `DELETE /api/v1/admin/integration-keys/{id}` revokes a credential.

Configure Studio through its protected server environment:

```dotenv
PublicSite__BaseUrl=https://api.bryanstudiosltd.com/
PublicSite__IntegrationToken=<managed integration key secret>
PublicSite__PollingEnabled=true
PublicSite__GalleryPushEnabled=true
PublicSite__BookingCatalogPublishingEnabled=true
PublicSite__SelectionPollingEnabled=true
```

All four worker flags default to disabled. The single configured token needs the scopes of the enabled features:

| Feature | Scopes |
| --- | --- |
| Intake and access-request polling | `intake:pull`, `intake:ack`, `gallery:access:pull`, `gallery:access:ack` |
| Gallery staging, publication and revocation | `gallery:publish` |
| Catalog and slots | `booking:catalog:publish`, `booking:availability:publish` |
| Photograph selection polling | `gallery:selections:pull`, `gallery:selections:ack` |
| Client identity proofs | `client:link` |

Publish actual Studio sessions, opening hours and resource-backed availability before accepting bookings. Studio continues to operate locally during API outages. Gallery errors are retained in `studio_gallery_exports.last_error`; failed export attempts rotate behind older pending work. Upload retries use stable publication/version IDs and skip bytes already stored. Review conversation/message creation and export state commit together; retries can recover the review conversation left by the earlier nontransactional implementation. Revocation failures retain the prior state and retry without stopping other revocations. Front Desk rechecks publication state, approval, Job state and client ownership before issuing a publish request, so stale access requests cannot republish revoked or reassigned galleries.

## Client ID proof and remaining identity blocker

Studio already has an Administrator-only Management UI and `/api/admin/gallery-client-links` routes to select an existing client, create a proof, inspect status and revoke an unused proof. Studio calls `/api/v1/admin/client-link-proofs` using its **`client:link` bearer scope**, not the bootstrap provisioning header. Codes expire after 15 minutes and are single use. Deliver the code through a verified private channel.

The signed-in client redeems the code with same-origin `POST /api/v1/client-link-proofs/redeem`. Ownership is checked using a server-side provider-subject/client binding. `CLIENT_BINDINGS_JSON` is a legacy server-side mapping, not a replacement for verified production identity.

**Production social/OIDC sign-in is still unimplemented.** The session reader currently recognizes only fixed demo identities. Keep `PUBLIC_DEMO_MODE=false` publicly; startup rejects demo mode when `NODE_ENV=production`. Do not open real client galleries until production sign-in, provider-subject verification and two-client isolation are implemented and tested. Thumbnail/preview/download currently serve the same stored raster bytes; separate proof transformations are also unimplemented.

## Controlled acceptance before release

Use test clients only. Verify restart persistence, partial media transfer, lost stage/publish/revoke responses, rollback/retry of review creation, cross-client denial, revocation and backups. Submit photograph selections, interrupt Studio after its local import but before API acknowledgement, restart and confirm exactly one stored snapshot and one staff message. Invalid client/publication/version combinations must remain unacknowledged and be investigated. Repeat with a later selection snapshot and verify the original remains immutable.

AWS deployment, infrastructure, a container build/runtime check, production identity and controlled live acceptance remain release prerequisites. This change does not deploy, publish an image or enable production workers. Review the feature branch before merging.

## Local verification recorded 2026-10-24

Environment: this Mac, Node v24.22.1, .NET SDK 10.0.402, disposable PostgreSQL 18 on loopback port 55439. No AWS operations or cloud builds were run.

- Reference Site: `npm test` — 27 passed, zero failed/skipped.
- Final affected Studio tests: `dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore -m:1` with the filter below and local PostgreSQL — 34 passed, zero failed/skipped. This builds Studio and its test project locally; all seven `ReferenceGallerySyncTests` execute against PostgreSQL.
- Full Studio Web suite with serialized test collections: 415 passed, 10 failed, zero skipped. Unchanged `main` (`ff089c8f057a9d623927addee425237b75e06a7e`) in a temporary local checkout: 408 passed, the same 10 failed, zero skipped. Existing outcomes match, with seven added passing gallery tests.
- Full UI suite after local `npm ci --ignore-scripts --no-audit --no-fund`: 188 passed, 87 failed, zero skipped. The same suite on unchanged `main`, using the same installed dependencies, has identical counts and failure names. The standalone browser change only corrects the selection receipt text; no Studio UI/CSS renderer was added.
- PowerShell validation script parses successfully and now uses `npm ci` for the Reference Site. `git diff --check` passes. The full solution validation script was not run.
- Docker is unavailable in this environment, so the image build, runtime listener, volume ownership and scanner integration still need a local container check on a Mac with Docker. Production provider authentication and controlled live acceptance remain unverified.

Affected Studio test filter:

```text
FullyQualifiedName~ReferenceGallerySyncTests|FullyQualifiedName~IntakeCompletionTests|FullyQualifiedName~FrontDeskBookingRequestTests|FullyQualifiedName~GalleryProofTests|FullyQualifiedName~BookingOverrideRulesTests
```

The broader failures are pre-existing and still block claiming a fully green repository. They include incomplete scheduling/messaging database fixtures, a preview reflection signature mismatch and unrelated UI expectations. The first parallel Web run also exhausted PostgreSQL lock memory; the recorded comparison uses `--settings /private/tmp/aegis-reference-tests.runsettings` with test collections serialized:

```xml
<RunSettings><xUnit><MaxParallelThreads>1</MaxParallelThreads><ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit></RunSettings>
```

Local evidence: `/private/tmp/aegis-reference-results/` contains the baseline/full/final affected TRX reports; `/private/tmp/aegis-reference-ui.log` and `/private/tmp/aegis-reference-baseline-ui.log` contain the UI runs. These temporary files are not committed. Review and address the baseline suite failures separately before declaring the whole repository release-ready.

## Selection polling follow-up verification, 2026-10-24

Fresh local runs for the PR #258 follow-up independently reproduce the earlier baseline failures. The comparison checkout was clean, detached at fetched `origin/main`, `ff089c8f057a9d623927addee425237b75e06a7e`. Both checkouts used this Mac, Node 24.22.1, .NET SDK 10.0.402, the same installed UI dependencies and sibling Common projects. Database tests used a separate disposable PostgreSQL 18 cluster on `127.0.0.1:55440`, with isolated schemas and serialized xUnit collections.

| Local suite | Feature branch with follow-up | Unchanged main |
| --- | --- | --- |
| Studio Web, full suite | 418 passed, 10 failed, 0 skipped | 408 passed, 10 failed, 0 skipped |
| UI, full suite | 188 passed, 87 failed, 0 skipped | 188 passed, 87 failed, 0 skipped |
| Affected Studio filter above | 37 passed, 0 failed, 0 skipped | Not rerun separately |
| Reference Site | 29 passed, 0 failed, 0 skipped | Not rerun separately |

The full Web and UI runs match by **every failing test name and first error message**, with checkout paths normalized for UI errors. [Machine-readable comparison evidence](gallery-selection-baseline-evidence.json) records all 97 failures and their first errors. These suites remain failing; baseline equivalence does not establish that the unrelated behavior is correct.

Investigation of the 10 Web failures found two PostgreSQL `42601` errors from parameterized commands containing multiple statements, three missing `er.attendance_start_utc` column errors, two missing `studio_workflow_crew` table errors, one missing `external_message_id` column error, one missing `studio_schedule_entries` table error, and one stale reflection invocation of `MediaDerivativeWorker.Generate` with the wrong parameter count. The same names and errors occur on main. They concern existing SQL/schema setup and test invocation, outside the selection polling code.

The 87 UI failures consist of 21 missing-file errors (20 refer to the old root `roster-editor.js`, one to the old root `job-details.css`), 35 reference/type errors (including missing fixture globals and DOM elements, and the removed `StudioJobDetails.nextStep` function), and 31 assertion failures, including CSS ownership and legacy renderer expectations. The job-details stylesheet currently lives under `css/pages/`; `job-next-step.test.cjs` still calls the removed function. Matching baseline failures are recorded without changing these unrelated UI implementations or tests.

Polling retains its essential retry behavior: only a committed import is acknowledged; each item fails independently; subsequent pages still run; every new scan, including after restart or feed interruption, begins without a cursor. Cursors are scan positions, never durable success checkpoints. The follow-up rejects multi-page cursor cycles and malformed/noncanonical site cursors, requires the feed's `hasMore` flag, and checks cancellation before fetching or importing another item. Import uniqueness, transactional message creation and idempotent site acknowledgements are preserved.

New tests exercise 102 snapshots over three pages with simultaneous import and acknowledgement failures, a fresh worker retry, feed interruption after a committed import, timeout responses, cursor cycles, cancellation, site restart persistence, repeated acknowledgements and strict cursor parsing. Existing tests continue to cover snapshot/message rollback and changed-content rejection.

The request-direction audit traced Studio's `GalleryPushWorker`, `GallerySelectionWorker`, gallery administration and `PublicSiteClient`, plus all six Reference Site server modules and browser requests. Studio opens the HTTPS integration requests; redirects are disabled. The site exposes authenticated local store operations, contains no outbound fetch/request/connect calls or Kratos endpoint configuration, and serves a browser CSP with `connect-src 'self'`. The scoped HTTP selection handoff test and Studio handler host assertions passed. No Reference Site-to-Kratos integration was introduced or exercised; deployment networking was not inspected.

Commands used locally:

```sh
# Both checkouts; set STUDIO_TEST_POSTGRES to the disposable local database.
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -m:1 \
  --settings /private/tmp/aegis-reference-tests.runsettings \
  --logger 'trx;LogFileName=feature.trx' --results-directory /private/tmp/aegis-gallery-results/feature
# Main also used -p:CommonRoot=/Users/fitzroyf.wright/Projects/Common.
# Settings: <RunSettings><xUnit><MaxParallelThreads>1</MaxParallelThreads><ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit></RunSettings>
# The affected run used --no-build --no-restore and the filter above.
(cd tests/ui && npm test)
# Main used NODE_PATH pointing to the feature checkout's tests/ui/node_modules.
(cd Aegis.Studio.PublicSite && npm test)
git diff --check
```

Raw logs and TRX files are retained locally under `/private/tmp/aegis-gallery-*.log` and `/private/tmp/aegis-gallery-results/`. Sandbox-blocked initial build/listener attempts were rerun with local access; only completed runs are counted above. No PR merge, AWS operation, resource provisioning or cloud build was performed.
