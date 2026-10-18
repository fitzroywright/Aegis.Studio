# Complete workflow — local validation handoff

Use `feature/studio-complete-workflow-20261011`. Fetch the latest remote branch head: GitHub history contains the durable checkpoints. Do not substitute the older passing test counts for validation of the resumed changes.

## Local build and test commands

From the repository root, with the existing sibling Common projects available and the .NET 10 SDK:

```powershell
dotnet build Aegis.Studio.Web/Aegis.Studio.Web.csproj --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false
$env:STUDIO_TEST_POSTGRES = "Host=localhost;Database=studio_acceptance;Username=studio_test;Password=<local test password>"
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false
npm ci --prefix tests/ui
npm test --prefix tests/ui
node --test examples/public-v1-site/server.test.mjs
```

The PostgreSQL account must be able to create/drop isolated test schemas. Use a disposable test database; the suite creates and removes its own schema fixtures. Platform prerequisites and commands are documented in `scripts/Build-WorkDay.ps1` and the existing WorkDay platform documentation. iOS requires the supported Mac/Xcode provisioning setup.

No builds or automated tests were executed for the resumed changes. Only static JavaScript parsing and whitespace checks were performed.

## Configuration to exercise

- Assign separate Front Desk, Workspace Manager, Job Owner, Photographer, Production Manager, production assignee, Finance Manager and Operations Manager accounts. Repeat critical denials with a person who holds multiple roles. Administrator alone must not substitute for the named Finance/Production roles.
- Configure the existing storage provider and test originals, email identity and Microsoft Graph channel. Confirm the actual recorded workspace/person sender is used.
- For WhatsApp, set `Messaging:WhatsApp:Enabled`, `GraphApiVersion`, `PhoneNumberId`, and the access-token secret name. If using an approved deposit template, configure `DepositTemplateName`, `DepositTemplateLanguage` and pipe-separated `DepositTemplateParameters`; supported placeholders are `{client}`, `{job}`, `{amount}`, `{reference}`. Client destinations require explicit international format beginning with `+`.
- Configure Public V1 credentials/scopes, published-gallery access and webhook receiver using `PUBLIC-V1-INTEGRATION.md`. Keep API signing credentials in the reference server's environment.

## Connected acceptance scenarios

| Area | Scenario and evidence |
| --- | --- |
| Booking | Create a Pending Job, request a deposit through each configured channel, inspect queue/provider acceptance separately from client delivery, record a partial deposit, deny confirmation below the threshold, then confirm after payment or independent Operations approval. |
| Ownership | Assign an active Job Owner; accept and decline as that owner; deny another actor's response; reassign and reject the superseded response. Crew scheduling must wait for accepted responsibility. |
| Scheduling | Schedule multiple sessions with a Lead and multiple Additional photographers, partial attendance and equipment. Try staff/resource overlaps, leave, inactive resources, stale edits and a legacy two-person reschedule of a larger crew. Verify assignments and original Job ownership remain intact. |
| WorkDay | Accept/decline, arrive/start/complete, notes/problems and offline availability. Restart with queued reports; reconnect; simulate a lost acknowledgement and changed assignment generation; retain original conflicting reports in history and resolve with a reason. Never show an unacknowledged report as server success. |
| Studio manual reporting | Record individual photographer updates with actual time, source/reason and authenticated recorder. Completion by one photographer must not complete the whole shoot; completion by every current photographer must advance the Job. Retry the same operation and reject an altered recorder/payload. |
| Uploads | Declare a batch before storage. Interrupt mid-batch, reselect original files and resume with stable IDs. Verify stored hashes/sizes/version IDs, no duplicate versions, changed-byte rejection, late receipt replay after Job closure, unmatched return resolution and backup verification from actual receipts. Cancel an abandoned batch with a reason; preserve all stored files/history and invalidate its handoff proof. |
| Source handoff | Finish each required photographer contribution and ingest/backup tasks. Deny source submission with pending/failed/unmatched files, unverified backup or incomplete required sessions. Submit and review an exact version manifest; uploading a new version must invalidate prior verification and require renewed review. |
| Production | Deny production dispatch/source pinning/QC decisions to an owner or ordinary staff member. Permit assigned staff progress; permit Production Manager dispatch and technical approval. Amend specifications, reconcile exceptions, request corrections, and prove earlier approved versions remain immutable. |
| Previews | Verify bounded thumbnail/preview sizes and original preservation. Simulate decoder/storage failures, expired claims and explicit retries. An old claim must not overwrite the newer recorded derivative. |
| Finance | Exercise payment/refund, linked invoices and shared receipts, revocation, insufficient authorization amount, client changes and unpaid balances. Release only against current valid Finance authority; retain receivables after operational closure. |
| Client access | Demonstrate booking/status and published galleries through the reference website using Public V1 exclusively. Check reduced watermarked proofs, exact-version selections, revoked/expired access and Finance-gated final downloads. |
| Webhooks | Inspect signed payload/timestamp verification, durable receiver event IDs, retry/duplicate behavior and expired leases. Verify availability changes follow Finance changes. |
| Closure and legacy paths | Complete delivery and close, deny operational changes through connected and legacy APIs, then reopen as Operations with a reason. Financial records/history remain usable. Legacy automation cannot bypass human state/ownership/scheduling/production decisions. |

Outbound provider acceptance is not a delivery receipt. A provider may accept a request immediately before a process interruption; where the transport does not offer provider-side idempotency, a retry may produce a duplicate. Keep this outcome visible and reconcile with provider evidence.

## Record the result

Capture current commit, SDK/platform versions, passed/failed/skipped suites, migrations, representative Job IDs, stored version/receipt IDs, provider evidence and device results. Resolve failing source or acceptance cases and repeat the affected checks. This branch is not merged or deployed and is not a release candidate until local and connected/device acceptance succeeds.
