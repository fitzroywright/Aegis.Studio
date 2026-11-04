# Photography CRM implementation

The existing application uses .NET 10 minimal APIs, Npgsql/PostgreSQL, additive startup migrations and a JavaScript shell. `studio_clients` is the contact identity; jobs and enquiries already reference it. Sessions, job audit, financial events, invoices, quotes, galleries, production and outbound delivery have authoritative stores. Preserve those stores and job workflow states. CRM additions belong in `Crm`, the existing Clients renderer and its owning stylesheet.

Client scope is an explicit primary workspace or an existing linked job/enquiry workspace. Global CRM permission never bypasses workspace membership. Related operational records additionally require their own job permissions. Existing unscoped identities are administrator-only until assigned. Source merges retain the source record as an archived alias; linked operational records and immutable audit snapshots must survive. No name-only automatic merging.

The directory uses database pagination, whitelisted multi-sort and grouped financial/job projections. Lifetime value means net recorded receipts (JMD), never invoice totals. Cross-client combined invoices cannot safely be apportioned and must be labelled separately. No external channel is enabled by CRM.

Deployment uses Aegis.Operations/scripts/Build-Deploy-Studio-Kratos.ps1. Before live startup migrations: verified database backup, exact target/version and compatible additive schema. No demo reset against production.

## Implemented behavior

The directory has all eleven requested columns, prefix/name/company/contact search, indexed email/telephone duplicate matching, multi-sort, workspace/type/status/owner/inactivity/follow-up filters, personal saved views, pagination, column preferences, bounded CSV import/export and permitted bulk tagging/status changes. CSV imports validate every row and commit atomically; spreadsheet formulas are escaped on export. Jamaican local telephone numbers normalize with the +1 prefix for identity comparison.

Profiles have Overview, Jobs, Sessions, Communications, Financials, Galleries, Documents, Activity, Follow-ups and Scheduled messages. Existing conversations, notes/calls, job conversations and outbox states are projected without inventing delivery receipts. Contact/branch relationships reuse stored identities. Duplicate merges require permission, a fresh preview token and a reason; aliases, snapshots and historical audits remain. Related operations remain in their original modules. Legacy Front Desk client navigation now uses the canonical CRM page.

Eleven granular CRM permissions extend Staff and Roles. The client primary workspace is authoritative; otherwise existing linked workspace membership determines visibility. Jobs and financial/attachment/conversation data receive their own access checks. Legacy client and quote APIs enforce the same boundaries. Explicit consent blocks queued deliveries and retries as Attention, and a blocked lease is never submitted to a provider.

Existing workflow definitions/runs/leases/logs execute CRM actions. Workspace CRM automation starts disabled. Durable events cover client/enquiry/lead/quote/booking/session/gallery/job activity and scheduled inactivity/session/payment checks. Actions create follow-ups/reminders, assign a CRM owner, add tags, notify staff in-app or flag attention. Action receipts commit with their effects, preventing duplicate actions after recovery; metadata changes do not recursively publish client-created events.

## Database and rollout

`Crm/CrmSchema.sql` is embedded and runs in transactional, repeatable dependency batches. Versions 1–4 extend clients/conversations/workspaces and add CRM contacts, tasks, activity, saved views, events and action receipts. B-tree/GIN indexes cover scope, identity, text search, owners, tags, task due dates and timeline access. Existing IDs, records and financial ledgers remain intact. Existing client-since dates derive from the earliest linked job where available.

The existing workflow runner permits a nullable job reference for client-only runs. Default-disabled workspace automation means deployment does not immediately create such runs. Before enabling, review rollback compatibility: an older binary cannot interpret client-only workflow runs. Preserve those runs and use a compatible binary/forward fix rather than deleting audit data to roll back. Production needs a verified PostgreSQL backup and reviewed package/configuration backup before startup migration. No live schema writes have been performed in this mission.

## Verification and limits

Local verification includes the full Web and Domain suites with a disposable PostgreSQL cluster, CRM/Front Desk/attention/gallery UI tests, the CSS ownership guard, authenticated HTTP profile/API checks, and actual desktop/mobile Chrome renders. The full UI suite has existing failures in booking, announcements, media and administration; they reproduce on the original commit. One template-back-navigation test failed during the parallel run and passed in isolation. No baseline tests were disabled or weakened; demo fixture prerequisites were expanded without removing assertions.

The HTTP smoke environment uses its own schema, temporary storage, explicit external-integration isolation and the supported offline mock-administrator bootstrap. All CRM section APIs and liveness return 200. Readiness remains 503 for missing external/deployment configuration. Fresh empty-database startup reaches an existing earlier migration-order error, so the smoke schema was initialized with existing module migrations first.

External gallery download/expiry analytics are unavailable in the existing publication projection and are labelled accordingly. Documents use existing job attachments. Direct/private message threads without an authoritative job/client association are excluded. Client reminder sending uses existing provider/channel configuration and job consent; CRM automation notifications are staff in-app notifications. No new external transport is enabled.

Kratos is reachable as the configured deployment account and `aegis-studio` is active in `/opt/aegis/studio`. That account cannot inspect the package directory or verify backups; `sudo -n` returns “a password is required.” The established deployer therefore cannot safely complete backup verification, migration, installation or restart in this session. The installed revision and live CRM behavior remain unverified. Do not describe this release as deployed.
