# Aegis Studio Public Site deployment

`Aegis.Studio.PublicSite` is a standalone Node 24 service for the public Booking and Client Galleries experience. It communicates with Studio using the signed Public V1 API. It does not use Studio's database, filesystem, or object storage.

## Choose the deployment mode

| Mode | Use | Data behavior |
| --- | --- | --- |
| `PUBLIC_DEMO_MODE=true` | Internal preview and UX review | Fake Front Desk receipts, two fake social identities, and in-memory fake galleries. No Studio API requests are made. Never use for real client data. |
| `PUBLIC_DEMO_MODE=false` | API integration staging | Booking requests/status use Studio Public V1; gallery requests use Studio-issued per-gallery bearer tokens. Fake sign-in is disabled. |

The project currently contains mock Google/Facebook identities only. Real provider OAuth/OIDC callback and verified-subject-to-gallery-token binding are required before enabling real client sign-in. Do not expose private client galleries publicly until that work is complete. API integration staging can validate bookings and can validate gallery proxy calls once a trusted session callback is available.

## Prerequisites

- A host with Docker support, or Node.js 24.
- A DNS hostname and HTTPS ingress for public access.
- For API integration staging: an HTTPS Studio host with Public V1 enabled and reachable from the public-site host.
- Studio integration credentials provisioned using [`PUBLIC-V1-INTEGRATION.md`](PUBLIC-V1-INTEGRATION.md).

## Build and run a preview container

From the repository root:

```sh
docker build -t aegis-studio-public-site:0.1.0 -f Aegis.Studio.PublicSite/Dockerfile Aegis.Studio.PublicSite
docker run --rm -p 8080:8080 \
  -e NODE_ENV=production \
  -e PUBLIC_DEMO_MODE=true \
  -e SESSION_SECRET="$(openssl rand -hex 32)" \
  aegis-studio-public-site:0.1.0
```

Open `http://localhost:8080`. The social sign-in options are deliberately labelled as demo accounts. Maya Chen's session is bound to Maya's gallery; Jordan Lee's session is bound to Jordan's gallery. A user cannot switch galleries by changing a URL or request body.

For an actual deployment, push the image to your container registry, then create a service from that image. Set the port to `8080`, configure the health check path as `/health`, and route the public hostname through HTTPS ingress. Only expose the ingress; do not publish the container port directly to the Internet. Require TLS, apply platform request-size/rate limits, and send Node stdout/stderr to the platform logs.

Example generic registry flow (replace the placeholders with the selected registry):

```sh
docker tag aegis-studio-public-site:0.1.0 REGISTRY/aegis-studio-public-site:0.1.0
docker push REGISTRY/aegis-studio-public-site:0.1.0
```

Set `SESSION_SECRET` to a unique random value through the host's secret manager. For multiple replicas, use the same secret on every replica so stateless sessions continue to validate. Rotating it signs out all users. The app has no database or shared volume requirement in demo mode.

## Connect booking to Studio

1. In Studio, configure the `website` Public V1 integration client with `bookings.write` and `bookings.read` scopes. Provision its secret through the installed Common.Secrets provider.
2. Make the corresponding secret available to the public-site service as `STUDIO_SECRET` through the deployment host's secret manager; set `STUDIO_CLIENT=website`.
3. Set `STUDIO_BASE_URL` to the Studio HTTPS origin, for example `https://studio.example.com`.
4. Set `PUBLIC_DEMO_MODE=false` in an integration staging environment.
5. Submit a test booking and confirm that it appears in Studio Front Desk as a pending request. Confirm its response remains `received` until staff review and the configured deposit condition are satisfied.
6. Check status with the returned booking reference. Confirm the public response contains no workspace or staff information.

Keep `STUDIO_SECRET` server-only. The browser receives only same-origin routes and never receives the integration key. The public site signs a fresh nonce for every Studio request and reuses the same idempotency key only when retrying the same booking submission.

## Connect gallery data

Studio's existing client-access action issues a token scoped to one Job/gallery. Store the grant server-side with the verified social provider subject, gallery reference, and access token. Never put the token in the browser, a public link, a query parameter, or frontend JavaScript. Studio still decides which proofs are published, which selections are saved, and whether final delivery is authorized.

Before the public launch, complete and verify provider OAuth/OIDC. The callback must validate issuer, audience, state, nonce, signature, and provider subject, then map that verified subject to only the matching Studio gallery grant. Add an integration test proving one signed-in social subject cannot request another client's gallery or proof. Keep `PUBLIC_DEMO_MODE=false`; do not re-enable fake identities on a public production deployment.

## Operations

- Deploy a new immutable image tag for releases; keep the previous tag available for rollback.
- Check `/health`, service logs, and Studio's Public V1 operations/webhook health after rollout.
- Use a test client/gallery before production acceptance. Validate booking retries, unauthorized gallery references, proof access, selections, payment-gated downloads, and authorization revocation.
- No Studio storage is mounted into this service. If a later version adds a durable webhook inbox or cache, give it its own managed storage, backups, and retention policy.

## Rollback

Route the service back to the previous known-good image tag. This does not alter Studio bookings or gallery data because those records remain authoritative in Studio. Invalidate `SESSION_SECRET` only if session invalidation is part of the incident response; rotation logs users out.
