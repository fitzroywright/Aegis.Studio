# Public Site deployment and Client ID proof

## Build and verify on the Mac

From the repository root:

```bash
cd Aegis.Studio.PublicSite
npm ci
npm test
npm start
```

The contract example test is rooted at `../docs/public-site-v1/contract-example.json`; run tests from this project directory in the full repository checkout.

## Prepare the Kratos service

1. Merge/review the feature branch only after the Mac build and tests pass.
2. Build the container on the Mac from the reviewed commit and transfer/push it to the image registry used by Kratos. Keep the tag tied to that commit.
3. On Kratos, create a persistent data directory owned by the service account and store runtime values in the protected service environment or secret manager:
   - `NODE_ENV=production`
   - `PUBLIC_DEMO_MODE=false`
   - a unique `SESSION_SECRET` (32+ characters)
   - `INTAKE_DB_PATH=/app/data/intake.sqlite`
   - `PUBLIC_API_PROVISIONING_TOKEN` (random 32+ characters; same value configured only on Studio server side)
   - `PUBLIC_API_KEYS_JSON=[]` for legacy keys only; issue current credentials through the managed key endpoint
   - real OIDC configuration and callback URLs once the production provider is implemented
4. Mount the persistent data directory at `/app/data`, allow inbound HTTPS through the reverse proxy, and configure the public host. Do not expose the Node port publicly. Back up SQLite using its online backup mechanism or stop the service before copying the database and WAL files.
5. Configure DNS/TLS for the chosen API host, then deploy the image and verify `GET /health` and `GET /api/public-site/v1/openapi.json`.

## Issue API credentials

Key provisioning uses the server-side `PUBLIC_API_PROVISIONING_TOKEN` header. Never put that value in browser code or send it to an external website developer. The endpoint returns each key secret once; copy it directly into the appropriate Studio server secret/configuration and record the key name/scopes in the operational inventory. Do not paste secrets into logs or tickets.

- `POST /api/public-site/v1/admin/integration-keys` with `{ "name": "...", "scopes": ["..."] }`
- `GET /api/public-site/v1/admin/integration-keys` lists metadata only
- `DELETE /api/public-site/v1/admin/integration-keys/{id}` revokes a key

Likely separate credentials: website booking submission (`intake:submit`); Studio polling/acknowledgment (`intake:pull`, `intake:ack`); Studio gallery publisher (`gallery:publish`). Provisioning administration is a server-to-server bootstrap operation. A Studio Admin UI proxy is still required before this is managed through Studio pages.

## Client ID proof

1. Studio Admin must request a proof for an existing Studio Client UUID using the server-side provisioning token:
   `POST /api/public-site/v1/admin/client-link-proofs` with `{ "clientId": "<Studio Client UUID>" }`.
2. Give the returned one-time code to the client through a verified Studio-controlled channel. It expires after 15 minutes. Do not send the code in a URL or log it.
3. The client signs into the public site using the real provider account. The authenticated session redeems the code with same-origin POST `/api/public-site/v1/client-link-proofs/redeem` and JSON `{ "code": "..." }`.
4. Gallery authorization uses the verified provider + immutable provider subject binding to the Studio Client UUID. The site ignores caller-supplied client IDs. A code is single use; expired, revoked, replayed, or conflicting links fail closed.
5. Verify the correct client can view their published gallery and another client cannot, then revoke and verify access is denied.

This branch includes proof storage and redemption against the existing demo session's stable test identity. Production social/OIDC authentication and the Studio Admin UI that creates and presents proofs are not yet implemented. Do not enable real client galleries until those two pieces are complete and provider-subject verification is tested end to end.
