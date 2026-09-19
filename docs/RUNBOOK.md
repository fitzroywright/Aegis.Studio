# Aegis.Studio Operational Runbook

## Purpose
Provides Studio workspace messaging, bookings, attachments, notifications and gallery publication.

## Dependencies
PostgreSQL, Microsoft Graph/mail routing where enabled, Common.Messaging, Common.Storage, Common.Secrets and application authorization stores.

## Startup
Verify database initialization, storage health, diagnostics registration and configured external messaging providers.

## Health Verification
Inspect workspace-message flows, attachment-processing flows, gallery-publish flows, provider queue health and application diagnostics.

## Normal State
Inbound routing persists to the correct workspace; attachment storage is writable/integrity-checked; gallery publication is reachable; outbound provider queues drain without dead letters.

## Messaging Semantics
A completed provider dispatch is ProviderAccepted unless delivery receipts prove Delivered. Do not label provider acceptance as recipient delivery.

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
