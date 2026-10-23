# Public website integration

The authoritative contract is `Aegis.Studio.PublicSite/public-site-v1.openapi.json`. It is also served at `/api/v1/openapi.json`. This contract belongs to the public website, not the legacy Studio `/api/public/v1` endpoints. The production API is `https://api.bryanstudiosltd.com/api/v1`.

The website stores inquiries and booking requests independently. Its browser form and external submissions share one durable intake service and reference allocator. Studio connects outbound every 60 seconds, imports changes, then acknowledges the exact imported revision. Studio never needs an inbound booking port.

## Credentials

Configure `PUBLIC_API_KEYS_JSON` as an array of objects with `id`, `secret`, and `scopes`. Use a unique ID and random secret of at least 32 characters for each integration. Provision secrets through the deployment secret store, never through JavaScript, source control, URLs or email logs.

| Integration | Scopes |
| --- | --- |
| External booking website | `intake:submit` |
| Studio importer | `intake:pull`, `intake:ack` |
| Studio gallery publisher | `gallery:publish` |
| Verified payment adapter | `payment:verified` |
| Attachment scanner operator | `attachment:scan` |

Studio currently uses one configured integration token; it can carry the three Studio scopes if polling and gallery publication run together. External submitters must not receive payment, polling, acknowledgment or publication privileges. The verified payment adapter must verify the provider's result before using its credential. Studio and this site do not process card details. Rotate keys by adding a replacement credential, updating the consumer and verifying it, then removing the old credential. Preserve the credential `id` when rotating a secret to retain ownership and idempotency scope.

## Submission and receipt

POST `/intakes` using bearer authentication, JSON, and `Idempotency-Key`. See `booking-example.json`. For an inquiry, use `kind: Inquiry`, contact details and notes; service and times are optional. Bookings require email, service, location code and a valid start/end range. New location codes are `BK` (Kings Plaza) and `BE` (East Queen Street). Times use ISO 8601 with explicit offsets and an IANA `timeZone`. Bryan's default is `America/Jamaica`.

The response includes an immutable UUID `id`, the legacy UUID `reference`, the client-facing `intakeReference` such as `2026-BK-000001`, a creation time, initial receipt/import state and a private per-intake `accessToken`. Never replace a UUID field with the short reference. The reference is not a credential. Browser status requests use `X-Intake-Token`; authenticated submission integrations can read only their own intake records. New bookings require `locationCode`: `BK` (Kings Plaza) or `BE` (East Queen Street). They receive `YYYY-BK-######` or `YYYY-BE-######`; inquiries retain `YYYY-W-######`. Each location and inquiry prefix has a separate sequence that restarts at `000001` in the Jamaica calendar year. Previously issued `YYYY-BR-######` references remain valid for status lookup and import. Idempotent retries return the original reference. The intake reference and original integration reference remain separate.

Website receipt means **saved on the website, awaiting Studio import/review**. It does not confirm a booking, availability, payment or email delivery. `locationCode` identifies the requested Studio location; it does not confirm resource availability. Website intake still awaits Studio review and scheduling. The legacy `requiredDeposit: 0` is a compatibility placeholder, not a quote or waived-deposit policy. Front Desk determines the authoritative price and deposit.

Replaying the same credential ID/key and payload returns the original result and reference, even after restart. A changed payload under the same key returns 409. Keys and results are retained durably in the site database; do not delete replay history while callers may retry. Third-party callers cannot choose reference numbers or counters.

## Polling and acknowledgment

GET `/changes` without a cursor for the first batch. Persist `nextCursor` only after records are durably imported or durably staged for retry. Feed pages contain at most 50 ordered changes, including roster, attachment-scan and payment changes. The cursor is opaque. Old revisions may replay; never create a second business record.

POST `/intakes/{id}/ack` with the exact imported `revision`. Older acknowledgment replays do not overwrite newer status. Acknowledging revision 1 while revision 2 exists leaves `pendingChanges: true`. Studio also sends review, booking and acknowledgment updates through this endpoint. These states are independent. Submission credentials cannot call it.

## Rosters

Use `roster-template.csv` for spreadsheet entry. The browser editor supports CSV/XLSX upload, paste, column mapping, validation, correction and explicit save. XLSX formulas are rejected, not executed. The first worksheet is used; limits are 5000 subjects, 100 columns, 5 MB upload and 30 MB expanded workbook content. A missing email or phone does not block an opted-out subject.

Each subject has its own stable reference, name, collection, contact fields, optional schedule/location/notes, and `pickupNotification`: `None`, `Email` or `WhatsApp`. Contact can be a parent or representative. Email notifications require an email; WhatsApp requires international `+` format. No notification preference is inferred from contact presence. Unclassified is reserved for the system subject.

PUT `/intakes/{id}/roster` with the current intake revision and complete accepted rows. Supply stable references to update existing subjects. Omitted fields preserve existing Studio values on update; explicit empty values clear optional fields. Invalid commits fail atomically. Conflicting website changes after a staff edit enter roster review rather than overwriting the staff correction. A browser roster draft is a separate durable record, not another inquiry.

## Attachments

POST `/intakes/{id}/attachments` with filename and base64 bytes. Limits: JPEG, PNG or PDF; 2.5 MB each; ten files totaling 10 MB. Type and extension must agree with file magic. Files stay quarantined until a configured scanner reports clean. `MALWARE_SCANNER_PATH` points to a clamscan-compatible executable. An absent, timed-out or failed scanner never means clean. An authorized scanner operator can POST `/attachments/{id}/rescan` after fixing the scanner.

GET `/attachments/{id}` requires the importer credential and returns only clean bytes. Studio fetches this API ID, checks the hash/type, and runs its own `PublicSite:MalwareScannerPath` before adding the file to normal conversation attachments. Metadata imports continue while files remain quarantined. Both scanner configurations are required to release website attachments.

## Payments

POST `/intakes/{id}/payments` is restricted to the verified adapter. Supply event ID, provider, transaction reference, kind (`Deposit`, `Refund`, `Dispute`), positive integer `amountMinor`, `JMD` currency and an optional provider event time. A `Dispute` event means a provider-confirmed debit/reversal, not merely an informational opened case. The adapter must use a unique transaction reference for each distinct financial adjustment, including partial refunds.

Duplicate events do not post twice. Conflicting reuse is rejected or held for review. Amounts use integer minor units on the API and decimal JMD in Studio. Imported deposits, refunds and confirmed reversal events use the existing financial ledger. Payment metadata must be associated with a booking job; an inquiry payment enters import review. No client-submitted success flag is trusted.

## Galleries

Studio stages metadata at POST `/galleries`, uploads each pinned approved image with PUT `/galleries/{id}/media/{versionId}`, then POSTs `/galleries/{id}/publish`. The site publishes only after every declared item is stored. Retries repeat the same publication/version IDs. POST `/galleries/{id}/revoke` hides a publication. Publication ownership cannot change to another client.

The site serves its own copies; it does not call internal Studio for gallery files. Configure `CLIENT_BINDINGS_JSON` to map an authenticated identity ID to the Studio client UUID. A caller-supplied email or client ID is never used for ownership. Existing demo sign-in remains available only when explicitly enabled; production social sign-in still requires the deployment's identity integration. Do not enable demo login on a public production deployment.

## Errors and compatibility

Errors have `code`, `message`, `fieldErrors`, and `correlationId`. Correct 400/413 errors. Resolve 409 replay/revision conflicts by reading the current state; never silently invent a replacement identity after an uncertain request. For 429 honor `Retry-After: 60`. For network failures or 503 retry the same key/payload. Unknown outcomes require reconciliation. Do not log bearer tokens, private intake tokens, request bodies or roster contacts.

Compatible additive fields may extend V1. Breaking field, behavior or authentication changes require V2 and a migration plan. Do not freeze or distribute this contract as validated until the MacBook contract tests and controlled end-to-end checks pass.

## Local setup and validation

Use Node 24 and a durable writable directory. Start from `.env.example`; use unique local test credentials and no real customer data. Run `npm install` and `npm test` on the MacBook. Use `requests.http` for operation examples and `contract-example.json` as the shared site/Studio consumer fixture. Restore and run the .NET and UI suites through `scripts/Validate-ClientIntake.ps1`.
