# Aegis Studio Public Site

Standalone Node 24 website for Booking and Client Galleries, with its own durable SQLite database and media copies. The public site does not call internal Studio. Studio polls the versioned public-site API every 60 seconds and pushes approved gallery media to the site.

Copy `.env.example` to `.env`, provision local test secrets, and run on the MacBook:

```bash
npm install
npm test
npm start
```

The website accepts inquiries and bookings through its browser form and authenticated submission API. It saves before returning a receipt. Form and API share the W reference allocator and intake store. Acknowledgment, import, booking and payment states remain distinct. Rosters can be entered, pasted or uploaded as CSV/XLSX, reviewed and saved. Attachments remain quarantined until scanning passes.

See [the API guide](../docs/public-site-v1/INTEGRATION.md), [OpenAPI](public-site-v1.openapi.json), and [deployment and live proof runbook](../docs/CLIENT-INTAKE-HANDOFF.md).

## Storage and hosting

Use a persistent writable volume for `INTAKE_DB_PATH`, including SQLite WAL files. The database stores intake, replay keys, roster drafts, attachment quarantine, payment events, gallery metadata, media and selections. Back it up using SQLite's online backup mechanism or while the service is stopped. Do not copy only the main database file from an active WAL database. Restrict volume access and protect backups.

The Dockerfile uses Node 24 and runs as the unprivileged node user. Build the image on the MacBook, then deploy the resulting image behind HTTPS with a persistent `/app/data` volume. Pin deployment image tags to the reviewed revision.

## Identity and payment boundaries

Demo sign-in is explicitly enabled with `PUBLIC_DEMO_MODE=true` for local acceptance only. Production provider sign-in is not implemented by this intake change; retain the existing sign-in boundary and finish the production identity integration before opening real client galleries. `CLIENT_BINDINGS_JSON` maps an authenticated identity to a Studio client UUID, and the site checks that binding for every local gallery request. Never expose demo login publicly.

A verified payment-provider adapter can submit financial events with its dedicated `payment:verified` credential. Ordinary submitters cannot mark themselves paid. This site and Studio do not collect card details.

## Validation state

This branch contains source changes and regression tests. No restore, build, test, deployment, live payment or mail proof has been run in the implementation environment. Run the MacBook checks and controlled end-to-end acceptance before enabling the integration. The REST contract is a candidate V1 until those checks pass.
