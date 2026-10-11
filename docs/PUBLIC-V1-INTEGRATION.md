# Brian’s public website integration — V1

Implementation branch: `feature/studio-complete-workflow-20261011`. This is an integration implementation awaiting connected PostgreSQL, storage, webhook-destination and deployed acceptance. Do not treat unit tests or the reference proxy test as deployment acceptance.

## Installation configuration

Use the existing Common.Secrets provider to provision `BookingApi:Clients:website:Secret`. Keep this secret on the website server. Configure explicit scopes; an authenticated Studio cookie does not authorize V1 integration requests.

```json
{
  "BookingApi": {
    "RequiredDeposit": 1000,
    "Clients": { "website": { "V1Scopes": ["bookings.write", "bookings.read"] } }
  },
  "ClientGallery": { "Provider": "Manifest" },
  "PublicApi": {
    "Webhooks": {
      "Targets": {
        "website": {
          "Url": "https://www.example.com/webhooks/studio",
          "SecretName": "PublicApi:Webhooks:website:Secret",
          "Events": ["booking.received", "booking.confirmed", "gallery.published", "gallery.updated", "final.delivery.available"]
        }
      }
    }
  }
}
```

Provision the webhook signing secret separately through Common.Secrets. Destinations are installation configuration, use HTTPS and do not follow redirects. No SaaS tenant platform is introduced. No destination was configured or contacted during implementation.

OpenAPI: `/api/public/v1/openapi.json`. Source: `Aegis.Studio.Web/PublicApi/public-v1.openapi.json`.

## Integration authentication

Every booking request has these headers:

- `X-Studio-Client`: configured client key, such as `website`.
- `X-Studio-Timestamp`: integer Unix seconds, within five minutes of Studio.
- `X-Studio-Nonce`: a fresh value for every HTTP attempt, including retries.
- `X-Studio-Signature`: lowercase hexadecimal HMAC-SHA256.
- `Idempotency-Key`: required for booking submission; retain it when retrying the same booking facts.

The signed UTF-8 canonical string is six lines, with no final newline:

```text
UPPERCASE_METHOD
/api/public/v1/path?exact=query
TIMESTAMP
NONCE
lowercase_sha256_of_exact_request_body_bytes
IDEMPOTENCY_KEY_OR_EMPTY
```

The signature binds the method, complete path/query, payload and idempotency key. GET signs the SHA256 of an empty body. Studio persists consumed nonces and rejects replays. Cookies cannot substitute for signatures. A fresh nonce/signature combined with the original idempotency key retries the same business operation safely.

## Booking

`POST /api/public/v1/bookings` requires `bookings.write`. The OpenAPI request fields are customer name/email, session type and start/end timestamps, with optional telephone/location/notes/destination. Destination is routed by configured workspace email/route, with unmatched requests going to permanent Unassigned.

A booking immediately enters Front Desk as a Pending Job. The response is HTTP 201 with `reference`, `status`, `requiredDeposit` and `currency`. It contains no internal workspace, staff assignments or management details. Reusing a key with different facts returns HTTP 409. Reusing the same key/facts returns the original receipt.

`GET /api/public/v1/bookings/{reference}` requires `bookings.read` and returns only bookings submitted by that integration client. External status is one of `received`, `confirmed`, `ready`, `delivered`, `completed`, `cancelled`.

V1 does not expose payment-confirmation writes. Staff record verified manual receipts through Studio/POS. Sending a website request must never manufacture a payment.

## Customer gallery

Studio publishes an approved Finished submission from Job Gallery using the existing publication action. Issue customer access using the existing Job client-access action. The authorized response additionally supplies `publicV1.galleryReference`, `apiPath` and `accessToken`. Existing portal links remain supported. Customer tokens expire and can be rotated/revoked in Studio.

Use `Authorization: Bearer CUSTOMER_TOKEN` on gallery requests. The token authorizes only its Job. Do not place it in proof URLs or expose an integration secret in browser code.

| Operation | Path |
| --- | --- |
| Gallery / permitted selections | `GET /api/public/v1/galleries/{reference}?limit=50&after={versionCursor}` |
| Reduced watermarked proof | `GET /api/public/v1/galleries/{reference}/items/{version}/preview` |
| Reduced watermarked thumbnail | `GET /api/public/v1/galleries/{reference}/items/{version}/thumbnail` |
| Authorized final attachment | `GET /api/public/v1/galleries/{reference}/items/{version}/download` |
| Set a selection | `PUT /api/public/v1/galleries/{reference}/selections/{version}` with `{ "selected": true }` |
| Submit exact selected versions | `POST /api/public/v1/galleries/{reference}/selections/submit` with `Idempotency-Key` |

Page size is 1–100. `nextCursor` is null on the last page. Check `publicationId` while paging; a newly published revision starts a new selection scope. IDs identify exact versions; filenames are labels. The management Job exposes the resulting submission and exact version links.

Proofs are generated from the pinned published versions, reduced to at most 1200 pixels and visibly marked with repeated PROOF lettering. No original or private storage URL is included. Non-image originals remain preserved; unsupported image proofs report an error.

Final downloads recheck published technical approval, Job state, customer access and settlement/Finance authorization on the server. Finance authority must cover the current balance and same customer; revocation, refund/increased balance or customer change can remove access. Recording delivery goes through the same gate. Credit does not mark a balance paid. Legacy workspace galleries are proof-only because they do not identify a pinned Job submission/receivable.

## Errors and compatibility

Errors use `{ "code": "...", "message": "...", "requestId": "..." }`. Handle 400/401/403/404/409/413/415/429/503. Retry transport/503/429 responses with a fresh signature and the same business idempotency key; respect `Retry-After`. Do not retry corrected facts using an old key. JSON requests are limited to 64 KiB and public V1 has an independent persisted rate limit.

The V1 OpenAPI baseline is frozen in contract tests. Existing paths, response fields/types and enum values must remain compatible. Add optional fields within V1; incompatible changes require a new major path. Deprecation must be documented with a migration period and explicit developer notice before removal. Never change V1 to match an internal UI refactor silently.

## Webhooks

Webhook envelopes contain `schemaVersion`, stable `id`, `type`, `occurredAt`, and externally permitted `data`. Delivery is at least once. Persist event IDs and apply business effects exactly once at the receiver.

Verify HMAC-SHA256 over:

```text
TIMESTAMP
EVENT_ID
exact_request_body
```

Headers are `X-Studio-Timestamp`, `X-Studio-Event-Id`, `X-Studio-Signature`. Reject stale signatures, invalid HMACs and an event ID reused for different facts. A valid duplicate gets a success response after durable deduplication. Timestamp changes on retry, while the event ID and envelope stay fixed.

Studio stores events and per-destination delivery attempts in PostgreSQL, leases work across processes, retries with bounded exponential delay, and records endpoint acceptance/failure. Operations Management shows status and direct Job links. Endpoint acceptance means the receiver accepted the webhook; it does not prove a human read it. An availability event is advisory: always recheck final download authorization, which may have changed after the event was queued.

## Reference website

`examples/public-v1-site` is a small Node server plus browser page. It uses V1 exclusively. The server signs booking requests; the browser uses customer capability tokens for gallery/proofs/selections/downloads. Its webhook receiver persists and deduplicates signed envelopes before acknowledging them.

On the development machine, from the repository root:

```sh
export STUDIO_BASE_URL='https://your-studio-host'
export STUDIO_CLIENT='website'
export STUDIO_SECRET='set-through-your-secret-manager'
export STUDIO_WEBHOOK_SECRET='set-through-your-secret-manager'
node examples/public-v1-site/server.mjs
```

Open `http://127.0.0.1:8080`. A deployed website requires HTTPS reverse proxying. The example binds loopback and is an integration demonstration; add the website's normal authentication/session policy, abuse protection and business presentation before using it as Brian's public production site. Environment secrets must never be committed.

Verification performed: native contract/signature/authority tests and Node proxy/receiver tests with a stubbed V1 upstream. Real booking-to-gallery, remote webhook retries, payment/refund races and real Common.Storage media need connected installation acceptance.
