# Client intake implementation and validation handoff

Aegis.Studio main includes independent public-site intake, one-minute Studio polling, intake references, acknowledgment status, roster staging and review, subject communication and optional pickup reminders, provider acceptance persistence, and background gallery copying. The public API is served at `https://api.bryanstudiosltd.com/api/v1`. Restore, build, tests and live acceptance are run on the MacBook and controlled runtime; a source change alone is not deployment or operational proof.

## Update the MacBook checkout

Run inside the existing Aegis.Studio checkout, with the existing Common repositories available through `CommonRoot`. Keep using `main`:

```bash
git pull --ff-only origin main
```

## Local validation

Use .NET 10, Node 24, PowerShell 7 and a disposable local PostgreSQL database. Set `STUDIO_TEST_POSTGRES` to that database; the integration fixtures create isolated schemas. Never point it at the operational database.

```powershell
$env:STUDIO_TEST_POSTGRES = '<local disposable PostgreSQL connection string>'
pwsh -File ./scripts/Validate-ClientIntake.ps1
```

That script restores/builds the solution, runs .NET web tests, restores and runs public-site tests, and restores/runs the UI suite. It stops on failure. The workbook dependency has a committed lockfile; use `npm ci` for reproducible restores.

Manual browser acceptance must also exercise pasted cells, column mapping, error correction, save/reload, and the Front Desk/Job Details roster controls. Test both restricted and authorized users. A recipient with conversation-only access can see message status without obtaining full job/roster access. Existing subjects are not deleted through roster import.

## Configuration

### Public site

- `SESSION_SECRET`: random secret, at least 32 characters.
- `PUBLIC_DEMO_MODE=false` on production. Use true only for local mock identity tests.
- `INTAKE_DB_PATH`: persistent SQLite path, for example `/app/data/intake.sqlite` in the supplied container.
- `PUBLIC_API_KEYS_JSON`: dedicated credential IDs, secrets and scopes, as described in `docs/public-site-v1/INTEGRATION.md`.
- `MALWARE_SCANNER_PATH`: clamscan-compatible executable inside the hosting environment. Missing/unavailable scanner leaves attachments quarantined.
- `CLIENT_BINDINGS_JSON`: authenticated identity ID to Studio client UUID mapping. Real provider sign-in remains a separate production prerequisite.

### Studio service environment

```text
PublicSite__PollingEnabled=true
PublicSite__GalleryPushEnabled=true
PublicSite__SelectionPollingEnabled=true
PublicSite__BookingCatalogPublishingEnabled=true
PublicSite__BaseUrl=https://api.bryanstudiosltd.com/
PublicSite__IntegrationToken=PROVISION_THROUGH_THE_EXISTING_SECRET_STORE
PublicSite__MalwareScannerPath=/usr/bin/clamscan
```

The integration token needs `intake:pull`, `intake:ack`, `gallery:access:pull`, `gallery:access:ack`, `gallery:publish`, `gallery:selections:pull`, `gallery:selections:ack`, `booking:catalog:publish`, and `booking:availability:publish` when all workers are enabled. Add `client:link` for Management identity proof administration. Keep the existing Microsoft Graph tenant/client/secret resolver, authorized sender configuration, monitored Reply-To and Common.Storage settings. Missing scanner configuration does not block booking metadata.

The workers are disabled unless explicitly enabled. None needs an inbound Studio endpoint. Their clients use certificate-validated HTTPS, reject redirects, and retain durable retry state. PublicSite BaseUrl must be an HTTPS origin, not a path. Configure request size limits and HTTPS at the public ingress. Never expose integration secrets to browser code.

## Migration and compatibility

Startup applies additive schema changes. Back up PostgreSQL before deployment. Intake adds conversation references, annual counters, website import/retry/cursor records, staged rosters, attachment metadata and payment-event history. Subject pickup preferences and a subject readiness anchor extend existing records. The reminder scheduler/outbox remain canonical.

New Studio-origin conversations receive `YYYY-S-NNNNNN`; the public site issues BK/BE booking references and W inquiry references. Historical conversations retain their existing references and may show Legacy intake. Existing immutable IDs and AEGIS threading references remain valid. Imported W references are preserved and can resolve replies/search without granting access.

Website booking import creates the existing Pending Job/request structure, not a parallel workflow. Inquiry import saves contacts, matches at most one existing client and does not create a permanent client. A recognized existing client is preserved on a pending imported job. New-client creation is an explicit Front Desk action or reviewed booking action.

Subject readiness notifications use the existing subject production status **Needs Delivery**, never an assumed whole-job completion. Opt-in, valid current contacts, job state and fulfillment are checked again before dispatch. Repeated events do not create another automatic reminder. Contact/preference changes cancel pending deliveries; an already accepted email is never recalled or resent automatically. Deliberate new subject messages create separately audited operations and show selected recipients first.

The legacy Studio `/api/public/v1` interface remains for compatibility. It is not the new public website contract. The reference site never forwards bookings, gallery reads or photograph selections into internal Studio. Studio polls submitted selections and acknowledges them only after its local snapshot/message transaction commits. See `docs/public-site-v1/DEPLOYMENT.md` for current AWS us-east-2 hosting prerequisites and `INTEGRATION.md` for selection routes and scopes.

## Kratos deployment

Use the existing MacBook Studio build/deploy workflow from `main` after local validation. Keep its existing service account, storage permissions, secret files and service unit. This repository does not contain the organization-level Kratos deployment script, so no replacement service installer is introduced here.

1. Back up PostgreSQL and the public site's SQLite volume. Stop old public-site forwarding before enabling the new independent intake mode; do not run mixed old/new booking ingestion.
2. Build Studio and the public-site image on the MacBook. Use a revision-specific image tag and transfer that built image to the public host. Do not point the public site at internal Studio.
3. Deploy the public site with its own persistent `/app/data` volume and secrets. Configure HTTPS, health checks and scanner access. Verify `/health` and the OpenAPI route. Confirm a submission saves while Studio is unreachable.
4. Deploy Studio through the existing Kratos deployment script, initially with the new workers disabled. Confirm startup migrations and existing pages load. Configure the public origin and integration token in the existing protected service environment.
5. Enable polling, restart `aegis-studio`, and verify the controlled test intake imports exactly once. Enable gallery push after testing identity binding and two-client isolation. Keep production mock sign-in disabled.

Useful server log commands:

```bash
sudo journalctl -u aegis-studio --since "15 minutes ago" --no-pager -o short-iso-precise \
  | grep -Ei 'Website intake|Website attachment|Public gallery|Outbound|Provider|threading verification'
```

Use the delivery UUID shown beside an outbound message to filter its exact attempt/acceptance logs. Logs contain correlation identifiers, never credentials or message bodies. Preserve the first provider-acceptance timestamp even if threading verification fails.

## Controlled end-to-end acceptance

Use only a user-designated test recipient and test clients/subjects. No real-client test send is authorized by this implementation.

1. With Studio unavailable, submit an inquiry and booking through the site form and another booking through the authenticated external API. Capture the saved receipt and short reference. Replay the same key, replay a changed key payload, reload a saved roster draft, and verify privacy of status access.
2. Enable Studio polling. Confirm the same references, Website source, detailed origin, contact, service, schedule, roster and attachment statuses in Front Desk. Repeat a batch, lose an acknowledgment, restart the worker and change a roster during polling. No duplicate jobs, subjects or financial entries may appear.
3. Validate one opted-out subject without contact, one opted-in contact, duplicate references, conflicting staff/website edits, and an invalid workbook formula. Correct the preview before committing. Promote an inquiry to a job and verify exactly one copy of every accepted subject.
4. Test valid, rejected, quarantined and scanner-unavailable attachments. Metadata must remain visible. No normal attachment path may open quarantined bytes. Recover the scanner and rescan; only then may content become accessible.
5. Submit a provider-verified test deposit, replay it, then submit a distinct refund/reversal. Confirm existing ledger balances and no double posting. A paid request must remain pending until authorized availability/deposit confirmation.
6. Through the actual Front Desk client conversation, send a reply to the designated test mailbox. Record message/delivery IDs, UI status, correlated journal entries, acceptance time and persisted provider acceptance. Separately verify that the acknowledgment arrives in the test inbox with the correct reference/content. Reply as the test client and verify that the reply reaches the same inquiry/job.
7. Simulate delayed/unavailable Sent Items verification after accepted delivery. Status must stay ProviderAccepted, and the delivery must not re-enter the sending queue. Unknown pre-persistence outcomes must be held for reconciliation.
8. Mark one subject Needs Delivery while another remains unfinished. Only eligible opted-in subject contacts may be notified. Test withdrawal, contact changes, repeated events, completed pickup, shared contacts and deliberate manual resend.
9. Publish a pinned approved gallery, interrupt/retry transfers, verify client-specific access, and revoke it. No public request should depend on an inbound call to internal Studio.
10. Check Today-based layout, compact scrolling, subjects, navigation, shared unread/first-reader behavior and activity links. Opening an activity entry alone must not mark incoming content read.

## Evidence state

Prepared: source, migrations, candidate API contract, examples, roster template, regression tests, MacBook validation script and deployment checklist.

Historical handoff verification state is superseded by recorded local test results for the reviewed revision. Production deployment, browser acceptance, provider acceptance, client mailbox receipt, payment-provider verification and real identity/gallery isolation still require separate controlled evidence.

Do not describe the candidate API as validated for external developers, or intake as operationally proven, until the corresponding MacBook and controlled runtime evidence is recorded. Preserve this distinction in the pull request and operational handoff.
