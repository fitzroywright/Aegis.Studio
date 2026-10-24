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

The Studio service account must be able to write the enrollment file, defaulting to `/var/lib/aegis-studio/workday-enrollment-codes.json`. Protect that file as a secret. Deliver each one-time enrollment code through the studio's approved private channel; never put codes in source control or logs.

After changing the environment:

```sh
sudo systemctl restart aegis-studio
sudo systemctl is-active aegis-studio
sudo journalctl -u aegis-studio -n 100 --no-pager
```

Confirm that the exchange worker is running, a provisioned account receives its one-time code, first-time enrollment succeeds, snapshots appear in WorkDay Mobile, and an operation is acknowledged after Studio polls it. Test uploads and conversation replies with non-production data. Builds and automated tests should run on the developer Mac before deployment.
