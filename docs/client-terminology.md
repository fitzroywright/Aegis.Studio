# Client terminology

Studio uses Client consistently in the web app, WorkDay, API contracts, workflow states, demo generators and tests.

- The directory route is `#studio=clients&client=<id>`, with `StudioClients`, `clientId` and `studioClientId` used by navigation.
- Client directory assets are `clients.js` and `css/pages/clients.css`.
- Selection review uses `GET /api/jobs/{job}/client-selections`.
- Finance receivable entries expose `client`. WorkDay assignments expose `clientName`, `clientEmail` and `clientPhone`.
- The task status is `WaitingOnClient` throughout validation, workflow evaluation, workload queues and demo activities.
- Deposit WhatsApp templates use `{client}` for the client's name.

This is a direct rename without aliases or data migrations. Regenerate the demo data with the existing demo reset/regeneration action after deployment so persisted activities use the updated seed values. Existing demo markers intentionally continue to prevent automatic reseeding at startup. Deploy the web and WorkDay code together; refresh WorkDay's cached assignments from the updated server. Update any configured deposit template parameters to use `{client}`.
