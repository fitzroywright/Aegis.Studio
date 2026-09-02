# Aegis.Studio Booking MVP

Working branch: `chatgpt/booking-mvp-v1`.

The MVP provides public booking, photographer scheduling, team **Workspaces**, help-desk-style communications, background inbound-mail processing, PostgreSQL persistence, and Starfleet diagnostics.

## Run locally

```bash
docker compose up --build
```

Open `http://localhost:8080` for public booking and `http://localhost:8080/workspaces.html` for staff Workspaces.

## Communications model

A Microsoft 365 distribution address such as `weddings@bryans.com` can include the AegisHub mailbox. The inbound provider reads AegisHub, preserves the original recipient, and routes the message to the matching Workspace. Replies are sent through `IOutboundMailProvider` using the Workspace public address as the requested Send-As identity.

The current local provider logs outbound mail and returns no inbound messages. Microsoft 365/Graph, Bitwarden secrets, Slack, Teams, WhatsApp, gallery/storage, and mobile clients plug into the existing provider boundaries.

## Engineering rules

One class per file; small well-named methods; feature-oriented organization; dependency injection; composition over inheritance; explicit types; private fields use camelCase without underscore prefixes; Starfleet diagnostics is mandatory unless explicitly waived.
