# Aegis.Studio Operational Runbook

## Purpose
Provides Studio workspace messaging, bookings, attachments, notifications and gallery publication.

## Dependencies
PostgreSQL, Microsoft Graph/mail routing where enabled, Common.Messaging, Common.Storage, Common.Secrets and application authorization stores.

## Startup
Verify database initialization, storage health, diagnostics registration and configured external messaging providers.

## Health Verification
Inspect InboundMessage, OutboundMessage, NotificationFanout, AttachmentProcessing and GalleryPublish flows, provider queue health and application diagnostics. Notification branches are independent; one failed provider must not erase successful branch evidence.

## Normal State
Inbound routing persists to the correct workspace; attachment storage is writable/integrity-checked; gallery publication is reachable; outbound provider queues drain without dead letters.

## Messaging Semantics
A completed provider dispatch is ProviderAccepted unless delivery receipts prove Delivered. Do not label provider acceptance as recipient delivery.

Outbound customer email uses an explicit sending identity. Workspace identity is the default. Reply as Myself is offered only when the user has the workspace SendAsSelf capability and a valid profile email on the workspace mail domain or another explicitly allowed personal-sender domain. The server resolves the From address; clients may select only the identity type.

Microsoft Graph must be permitted to send as the selected mailbox. A successful Graph send advances the Provider stage to ProviderAccepted; the Recipient stage remains unproven unless a delivery receipt is available.

Review `/api/email/audit` for workspace-scoped message/conversation IDs, actual user, From address, sending identity type, status and timestamp. Message bodies are intentionally excluded from that audit view.

## Common Warnings
Unrouted mail, provider configuration waiting, retrying notification queue or slow attachment/gallery processing.

## Common Failures
Database/storage unavailable, repeated provider failures/dead letters, attachment integrity failure, or gallery publication failure.

## Recovery
Preserve queue and stored attachment/gallery state. Restore dependencies, allow durable queues to retry, then verify fresh correlated flow evidence.

## Backup / Restore
Back up PostgreSQL and storage roots consistently. Restore should preserve attachment/gallery metadata and immutable history.

## Deployment / Rollback
Keep registration identity, durable queues and storage roots across releases.

## Purge / Re-registration
Purge is separate from normal delete/version recovery. Re-registration uses Common.Registration lifecycle.

## Escalation
Escalate persistent mail-routing failure, storage integrity failure, provider queue exhaustion or cross-workspace authorization concerns.

## Log Locations
Application logs and Operations correlation traces. Never log mailbox secrets, access tokens, webhook secrets, registration credentials or attachment contents.
