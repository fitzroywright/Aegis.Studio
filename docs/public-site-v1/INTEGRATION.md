# Public website integration

The authoritative contract is `Aegis.Studio.PublicSite/public-site-v1.openapi.json`. It is also served at `/api/v1/openapi.json`. This contract belongs to the public website, not the legacy Studio `/api/public/v1` endpoints. The production API is `https://api.bryanstudiosltd.com/api/v1`.

The website stores inquiries and booking requests independently. Its browser form and external submissions share one durable intake service and reference allocator. Studio connects outbound every 60 seconds, imports changes, then acknowledges the exact imported revision. Studio never needs an inbound booking port.

## Credentials

Configure `PUBLIC_API_KEYS_JSON` as an array of objects with `id`, `secret`, and `scopes`. Use a unique ID and random secret of at least 32 characters for each integration. Provision secrets through the deployment secret store, never through JavaScript, source control, URLs or email logs.

| Integration | Scopes |
| --- | --- |
| External booking website API client | `intake:submit`, `gallery:access:submit` |
| Workday Mobile API client | `intake:submit` and Workday-specific scopes as those endpoints are added |
| Studio importer and publisher | `intake:pull`, `intake:ack`, `gallery:access:pull`, `gallery:access:ack`, `booking:catalog:publish`, `booking:availability:publish` |
| Studio gallery publisher | `gallery:publish` |
| Studio Client ID proof administrator | `client:link` |
| Verified payment adapter | `payment:verified` |
| Attachment scanner operator | `attachment:scan` |

Studio can use one configured integration token for import, booking publication, gallery publication and Client ID proof administration, with only the scopes it needs. Mark a Workday API credential's `clientApplication` as `Workday Mobile` when provisioning it; Studio records its submissions as `Workday Mobile`. The browser booking form's server-controlled route records `Web site`; callers cannot choose the source label in request JSON. External submitters must not receive payment, polling, acknowledgment or publication privileges. The verified payment adapter must verify the provider's result before using its credential. Studio and this site do not process card details. Rotate keys by adding a replacement credential, updating the consumer and verifying it, then removing the old credential. Preserve the credential `id` when rotating a secret to retain ownership and idempotency scope.

For Studio to synchronize the Public Site, configure `PublicSite:BaseUrl=https://api.bryanstudiosltd.com` and an integration token scoped to `intake:pull`, `intake:ack`, `gallery:access:pull`, `gallery:access:ack`, `gallery:publish`, `booking:catalog:publish`, and `booking:availability:publish`. Enable `PublicSite:PollingEnabled`, `PublicSite:GalleryPushEnabled`, and `PublicSite:BookingCatalogPublishingEnabled` as required. The session catalog, prices, opening hours, scheduling, and bookings stay in Studio's own database. When the API site is unreachable, Studio continues operating; publication retries in the background.

Public catalog and availability reads expose only the public session definition and open slots, so they do not require a bearer credential. The API still checks catalog revision, location, slot identity, and one-time reservation on every booking submission.

## Studio session catalog and availability

Studio is the source of truth for public session types, duration, setup and cleanup buffers, JMD price/deposit, eligible location and resource requirements. The Studio Admin page also manages weekly opening hours in Jamaica time. Day numbers are Sunday 0 through Saturday 6; a closed day has no row. Studio publishes the active catalog to `PUT /admin/booking/catalog` with `booking:catalog:publish`; publication uses an `Idempotency-Key` and increments the catalog revision. The public website reads `GET /booking/catalog` and renders only the active published entries. Money is integer JMD minor units (`priceMinor`, `depositMinor`), with 100 minor units per JMD dollar.

Studio derives bookable slots from its location hours, the catalog's buffers and resource requirements, existing room/equipment/staff schedule entries (including per-resource attendance windows), and photographer leave. Resource rules such as `Room-BK` and `Room-BE` keep location-specific rooms separate; `Photographer` matches active photographer schedule resources. Studio publishes stable slot IDs and the matching catalog revision to `PUT /admin/booking/availability` using `booking:availability:publish`. The browser lists unreserved slots using `GET /booking/availability?from=YYYY-MM-DD&to=YYYY-MM-DD&locationCode=BK&sessionTypeId=...`. Booking submission must carry the current `sessionTypeId`, `catalogRevision`, `locationCode`, and `slotId`; callers cannot supply their own duration or price. The API reserves a selected slot in the same durable transaction that saves the booking intake. A conflicting replay receives 409 and must choose another published slot.

## Submission and receipt

POST `/intakes` using bearer authentication, JSON, and `Idempotency-Key`. See `booking-example.json`. For an inquiry, use `kind: Inquiry`, contact details and notes; service and times are optional. Bookings require email, a published session ID and revision, location code and an available slot ID. The API supplies the stored start/end time and immutable price/deposit snapshot from Studio's current publication. New location codes are `BK` (Kings Plaza) and `BE` (East Queen Street). Times in the intake use ISO 8601 with explicit offsets and an IANA `timeZone`. Bryan's default is `America/Jamaica`.

The response includes an immutable UUID `id`, the legacy UUID `reference`, the client-facing `intakeReference` such as `2026-BK-000001`, a creation time, initial receipt/import state and a private per-intake `accessToken`. Never replace a UUID field with the short reference. The reference is not a credential. Browser status requests use `X-Intake-Token`; authenticated submission integrations can read only their own intake records. New bookings require `locationCode`: `BK` (Kings Plaza) or `BE` (East Queen Street). They receive `YYYY-BK-######` or `YYYY-BE-######`; inquiries retain `YYYY-W-######`. Each location and inquiry prefix has a separate sequence that restarts at `000001` in the Jamaica calendar year. Previously issued `YYYY-BR-######` references remain valid for status lookup and import. Idempotent retries return the original reference. The intake reference and original integration reference remain separate.

Website receipt means **saved on the website, awaiting Studio import/review**. It does not confirm payment, final approval or email delivery. The API site reserves the selected published slot atomically. When Studio imports it, Studio's same database transaction creates the pending FrontDesk booking and a resource hold against the matching active rooms, photographers and other configured resource types. Setup and cleanup buffers are included. Normal Studio scheduling treats that hold as occupied; scheduling the same Job is allowed to replace it with normal assignments. Holds stop blocking when their Job is Cancelled or Completed. A Studio Administrator can release a pending hold after rejecting/cancelling the request with `POST /api/admin/public-booking/holds/{bookingId}/release`, JSON `{"reason":"..."}`; `bookingId` is the API intake UUID. Price and required deposit are the exact published snapshot attached to that booking; subsequent catalog changes do not rewrite prior submissions. The Studio source label is `Web site` for browser bookings.

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

## Gallery access requests

The existing booking and inquiry contract remains unchanged. The External Site submits a client request using a server-side credential scoped to `gallery:access:submit`:

```http
POST /api/v1/gallery-access-requests
Authorization: Bearer <external-site-secret>
Idempotency-Key: <unique-request-key>
Content-Type: application/json
```

The JSON body contains `clientId`, `clientName`, and `clientEmail`; `galleryId` and `notes` are optional. The response means the request is queued, not that a gallery is published.

Studio polls `GET /api/v1/gallery-access-requests` with `gallery:access:pull`, imports each request into Front Desk, and acknowledges it at `POST /api/v1/gallery-access-requests/{id}/ack` with `gallery:access:ack`. Front Desk staff publish through Studio's existing `gallery:publish` permission. The External Site never receives gallery publication permission.

## Galleries

Studio stages metadata at POST `/galleries`, uploads each pinned approved image with PUT `/galleries/{id}/media/{versionId}`, then places the publication in Front Desk for staff review. Staff publication calls POST `/galleries/{id}/publish`; the site publishes only after every declared item is stored. Retries repeat the same publication/version IDs. POST `/galleries/{id}/revoke` hides a publication. Publication ownership cannot change to another client. Each later approved job creates a new gallery update in Front Desk for the same client; the additions remain private until staff publish them.

The site serves its own copies; it does not call internal Studio for gallery files. Studio administrators issue a short lived, single use Client ID proof from Studio Management after selecting an actual Studio client UUID. The Studio server verifies that UUID exists locally and asks the API to create the proof using a `client:link` scoped credential. Studio displays the secret once; only its hash is stored by the API. The client signs in to the public Gallery site and redeems the code there. The API binds its authenticated provider subject to the Studio client UUID; it never accepts a caller-supplied identity, email or gallery owner. Used proofs cannot be reused or revoked; identity unlinking requires identity administration. Active pending proofs can be inspected and revoked from Studio Management. Proof status never returns the secret or provider subject.

After redemption, the site's session resolves the bound Studio client UUID and lists only published galleries owned by that UUID. Gallery media is served from the API site's own stored copy; Studio remains the source of truth for approval and publication. The identity subject comes from the signed-in provider session, not browser input. Existing demo sign-in remains available only when explicitly enabled; production social sign-in still requires the deployment's real OIDC/provider integration and must be tested before public client use. Do not enable demo login on a public production deployment. Studio keeps operating locally when the API or internet is unavailable; identity proof creation/status and external gallery publication retry or wait for connectivity.

## Errors and compatibility

Errors have `code`, `message`, `fieldErrors`, and `correlationId`. Correct 400/413 errors. Resolve 409 replay/revision conflicts by reading the current state; never silently invent a replacement identity after an uncertain request. For 429 honor `Retry-After: 60`. For network failures or 503 retry the same key/payload. Unknown outcomes require reconciliation. Do not log bearer tokens, private intake tokens, request bodies or roster contacts.

No external developer has integrated the API yet. Keep one current V1 contract and update its examples and tests together; a parallel version is not required. Do not call the contract validated until the Mac contract tests and controlled end-to-end checks pass.

## Local setup and validation

Use Node 24 and a durable writable directory. Start from `.env.example`; use unique local test credentials and no real customer data. Run `npm install` and `npm test` on the MacBook. Use `requests.http` for operation examples and `contract-example.json` as the shared site/Studio consumer fixture. Restore and run the .NET and UI suites through `scripts/Validate-ClientIntake.ps1`.

