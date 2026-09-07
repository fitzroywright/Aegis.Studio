# Aegis.Studio Production Readiness

This document describes the runtime requirements for deploying the existing Studio feature set as an operational system rather than a demo.

## Required configuration

Do not place production passwords, Microsoft Graph client secrets, booking HMAC secrets, Slack/Teams webhooks, or database credentials in source control.

Provide these from the deployment secret store/environment:

- `ConnectionStrings__Studio`
- `MicrosoftGraphEmail__ClientSecret`
- `BookingApi__Clients__<client-id>__Secret`
- notification provider secrets/webhooks as applicable

Configure explicit durable storage paths for production:

- `Storage__AttachmentsPath`
- `Storage__GalleriesPath`
- `Storage__MediaArchivePath`

Local fallback storage is supported for a single-server installation, but an explicit backed-up path is strongly recommended. Multi-server deployment requires shared durable storage.

## HTTPS and reverse proxy

Production defaults require HTTPS. If Kestrel receives HTTPS directly, leave `Security__TrustForwardedProto=false`.

If TLS terminates at a trusted reverse proxy, configure the proxy to set `X-Forwarded-Proto: https` and set `Security__TrustForwardedProto=true`. Do not enable this when untrusted clients can connect directly to Kestrel.

## Production protections now enabled

Studio applies:

- correlation IDs on requests and responses;
- generic production error responses instead of stack traces;
- HSTS on HTTPS production responses;
- anti-framing, no-sniff, referrer, permissions, and baseline CSP headers;
- request-size limits;
- PostgreSQL-backed rate limits for login, public booking, payment confirmation, and public galleries;
- mutation audit records without storing request bodies or secrets;
- public gallery original downloads disabled by default;
- explicit authorization on sensitive email/security/account endpoints;
- explicit Job permission checks for P O S sale retrieval, completion, void, and refund routes;
- liveness and readiness endpoints;
- workflow failure inspection, log inspection, retry, and cancellation controls.

## Operations endpoints

Anonymous health probes expose status only:

- `GET /health/live`
- `GET /health/ready`

Administrators can inspect detailed readiness and operational audit history:

- `GET /api/admin/operations/readiness`
- `GET /api/admin/operations/audit?count=200`

Workflow operations:

- `GET /api/workflow-operations/summary`
- `GET /api/workflow-operations/failed`
- `GET /api/workflow-operations/waiting`
- `GET /api/workflow-operations/runs/{runId}/log`
- `POST /api/workflow-operations/runs/{runId}/retry`
- `POST /api/workflow-operations/runs/{runId}/cancel`

## Before go-live

Run:

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Then start the application with `ASPNETCORE_ENVIRONMENT=Production` and verify `/health/ready` returns HTTP 200.

Back up PostgreSQL and all configured attachment/gallery/media storage. Restore tests should be performed before treating backups as operationally valid.

Microsoft Graph should remain enabled in production. A not-ready Graph configuration is surfaced by the readiness check when Graph is enabled but credentials are missing.

Public booking HMAC authentication must have at least one configured client secret. Replay protection is persisted in PostgreSQL.

## Remaining infrastructure responsibility

Application hardening does not replace deployment controls. The production host should still provide OS patching, firewalling, TLS certificate renewal, PostgreSQL backup/monitoring, log collection, storage monitoring, and process supervision/restart.
