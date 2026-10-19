# Aegis Studio Public Site

A standalone Node 24 application for the public **Booking** and **Client Galleries** experience. The app uses the Studio Public V1 API for live booking and gallery data; it has no dependency on Studio's database or file storage. Its browser never receives the Studio integration secret. In demo mode it shows fake bookings, two fake social identities, and separate fake galleries.

## Run locally

```sh
cp .env.example .env
# Set SESSION_SECRET to a random value before running.
npm test
npm start
```

Open `http://localhost:8080`. `PUBLIC_DEMO_MODE=true` enables Maya Chen (Google) and Jordan Lee (Facebook) demo identities. Demo mode returns isolated fixture galleries and fake booking receipts. It must not be used for real client data.

Set `PUBLIC_DEMO_MODE=false` to use Studio's live Public V1 API. In that mode:

- `STUDIO_BASE_URL`, `STUDIO_CLIENT`, and `STUDIO_SECRET` configure the server-to-server signed integration.
- `STUDIO_CLIENT` must have `bookings.write` and `bookings.read` scopes in Studio.
- gallery grants must be added through the future authenticated social sign-in callback; the current demo login is disabled in live mode.
- Studio's Public V1 HTTPS endpoint and separate media store must be reachable from this app.

This initial project includes mocked provider sign-in only. Do not enable fake sign-in on an internet-facing production client portal. Before serving real galleries, complete provider OAuth/OIDC and bind each verified provider subject to a Studio-issued, per-gallery access token. Server routes already require the session's gallery reference to match each requested gallery, and never accept a browser-supplied access token.

## Container

Build from the repository root:

```sh
docker build -t aegis-studio-public-site:0.1.0 -f Aegis.Studio.PublicSite/Dockerfile Aegis.Studio.PublicSite
docker run --rm -p 8080:8080 \
  -e PUBLIC_DEMO_MODE=true \
  -e SESSION_SECRET="$(openssl rand -hex 32)" \
  aegis-studio-public-site:0.1.0
```

Deploy behind an HTTPS reverse proxy or managed container ingress. Set secrets in the platform's secret manager, use `PUBLIC_DEMO_MODE=false` for live API traffic, and restrict network egress to the Studio API and approved identity/media endpoints. For on-prem Studio, expose only the authenticated Public V1 API through a gateway; do not expose the Studio web application or storage directly. Configure a stable public hostname, TLS, request limits, and health-check `/health`. See [`docs/PUBLIC-SITE-DEPLOYMENT.md`](../docs/PUBLIC-SITE-DEPLOYMENT.md) for the deployment runbook.

## Studio API setup

Configure the Studio Public V1 integration client and secret as described in [`docs/PUBLIC-V1-INTEGRATION.md`](../docs/PUBLIC-V1-INTEGRATION.md). The Node server signs each request using the exact method, path/query, body bytes, timestamp, nonce, and idempotency key expected by Studio. Booking creation is idempotent and remains a pending Front Desk request until staff review and required deposit verification. Gallery requests use a Studio-issued client capability token kept server-side. Proofs, selections, and final-download authorization remain controlled by Studio.

No storage is shared between Studio and this app. Demo data is in code; live booking and gallery records/media are read or written only through Studio APIs. If webhook delivery is enabled later, persist its deduplication log on this app's own durable volume and keep it separate from Studio storage.
