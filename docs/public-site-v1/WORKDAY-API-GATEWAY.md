# WorkDay mobile API gateway

WorkDay's only server base URL is `https://api.bryanstudiosltd.com/api/v1/`. The app no longer accepts a configurable Studio hostname. The Public Site gateway permits only these WorkDay client operations and relays them to the existing Studio API over HTTPS:

| API Site path | Methods | Studio path |
| --- | --- | --- |
| `/auth/login`, `/auth/me`, `/auth/logout` | POST, GET, POST | `/api/auth/...` |
| `/workday/bootstrap` | GET | `/api/workday/bootstrap` |
| `/workday/device-session`, `/workday/device-session/revoke` | POST | Same Studio routes |
| `/workday/availability` | GET, POST | Same Studio route |
| `/workday/time-summary` | GET | Same Studio route |
| `/workday/operations`, `/workday/client-observation` | POST | Same Studio routes |
| `/workday/assignments/{id}/uploads` | POST multipart | Same Studio route |
| `/workday/assignments/{id}/gallery-status` | GET | Same Studio route |
| `/messages/threads...` | GET, POST | `/api/messages/threads...` |
| `/notifications` | GET | `/api/notifications` |
| `/conversations/{id}/sending-identities`, `/messages` | GET, POST | Same Studio routes |

The API Site rejects unlisted routes, including WorkDay fleet/management routes and Studio administration endpoints. It forwards the session cookie and required content headers. The Studio-issued WorkDay device token is forwarded only to field routes; shared messaging, notification and conversation routes use the authenticated Studio session cookie and do not receive the device token. Studio remains the system of record and performs its existing authorization, idempotency, storage and audit work. The API Site does not store WorkDay passwords or duplicate field records.

## API Site upstream configuration

Set these root-owned values in `/etc/aegis/public-site.env` on Kratos:

```dotenv
WORKDAY_STUDIO_ORIGIN=https://studio.bryanstudiosltd.com
WORKDAY_STUDIO_CONNECT_ADDRESS=127.0.0.1
WORKDAY_STUDIO_TIMEOUT_MS=120000
```

The origin must be an HTTPS origin whose certificate validates for `studio.bryanstudiosltd.com`. The connect address sends the request to Kratos's local Nginx listener while preserving TLS SNI and the Studio hostname. Do not set the upstream to the public API hostname; that would loop back into the gateway.

The WorkDay mobile app keeps its protected local SQLite cache, operation outbox, upload queue and offline behavior. If the API Site or Studio upstream cannot be reached, requests fail as unsynchronized and queued work remains available for retry. Studio's internal workflows do not use this gateway.

After changing the environment file, restart the API service:

```sh
sudo systemctl restart aegis-public-site
sudo systemctl is-active aegis-public-site
```

Then sign in from WorkDay and confirm bootstrap, an operation acknowledgement, availability read/write, messaging and one upload. Never test using production client data.
