# Offline Demo administrator maintenance

The Studio application bundle includes an OS-authorized maintenance command.
It starts no web listener, hosted workers, registration or notification services.
It uses the web application's content root, environment, appsettings and
`CommonSecretProviderFactory` to resolve the real Studio connection. Do not pass
connection strings or credentials on the command line.

Run from the selected application content root with its protected service
configuration loaded under the authorized service/maintenance identity:

```sh
dotnet Aegis.Studio.Web.dll maintenance bootstrap-admin \
  --login SELECTED_LOGIN --subject SELECTED_SUBJECT \
  --account-id REVIEWED_ACCOUNT_ID --actor OPERATOR_AUDIT_ID \
  --confirm-isolation
```

The default is a read-only database plan. It does not create security tables,
provision an account on login, infer an identity from a profile, or modify roles.
Use `maintenance --help` for syntax. The exact login and immutable subject must
already be in the server-controlled `MockAuthentication:Identities` allowlist.
For an existing account, retain its exact ID, alias and binding. For a new
account, generate and record its proposed ID before approval. Do not invent
subjects for existing bindings or import an Active account to bypass approval.

Both the plan and apply reject Production, disabled mock authentication,
unconfirmed configuration isolation, duplicate server identities, account or
binding conflicts, revoked identities, suspended/disabled accounts and inactive
Administrator membership. The existing global Administrator role must be active
and retain its unrestricted permission. The Job/Workspace Administrator template
does not substitute for that global role.

After reviewing the plan and receiving cutover authorization, stop Studio and
exclude all other identity-maintenance writers. Repeat the command with
`--apply --confirm-offline`. `--confirm-isolation` is an operator attestation
after reviewing the actual database, storage and integrations; it is not proof
that a deployment is isolated. Do not set it merely because a dataset looks
synthetic or an installation is called Demo.

## Persistence and recovery

The operation preserves the pinned shared account contract. Its phases are
separate audited shared-store transactions, not one transaction covering the
entire bootstrap:

1. Create a missing account and exact mock binding as Pending Access.
2. Persist the explicit Studio global Administrator membership through
   `ChangeAuthorization`, atomically with session-version invalidation and audit.
3. Explicitly approve Pending Access through the shared `SetStatus`, atomically
   with its session-version change and audit.

It revalidates the persisted state between phases and serializes bootstrap
commands with a database advisory lock. That lock does not replace stopping the
application and excluding other maintenance writers. A role or audit failure
cannot publish an unaudited role grant. If approval fails after role preparation,
the account remains Pending Access and operational access stays blocked. Retry
the same reviewed selection after addressing the failure. Successful retries
create no duplicate accounts/bindings and do not repeatedly change sessions or
audit. Existing unrelated roles, workspace/job permissions and business records
are retained. The tool never resets a password or silently relinks an identity.

Before live apply, verify package/configuration/key-ring, PostgreSQL and storage
backups, and restore into isolated database/storage/configuration. After approved
apply, verify normal `/api/auth/login`, current session binding, Active state,
explicit Administrator role, Staff & Roles, denied pending/ordinary access,
session/role revocation and logout/relogin. Denied cookie-authenticated API
requests return HTTP 403 rather than successful login HTML after a redirect.

Keep a tested package/configuration rollback and service restart trap. Preserve
the additive security records/audit on package rollback. Do not restore an old
database over newer business writes. Any identity conflict or unconfirmed
isolation keeps the deployment gate closed.

## Local preparation evidence

On the operator's Mac, the candidate passes 476 Web tests (including 10 new
PostgreSQL bootstrap cases and 3 API-denial cases), 3 Domain tests and 513 UI
tests, without failures or skips. A representative backup restored successfully;
planning, apply and retry were exercised on that isolated restore. Comparing all
185 existing business tables immediately after bootstrap found only the intended
global Administrator membership change. Shared account, binding, session-version
and audit records were added through the shared mechanism.

Normal HTTPS endpoint rehearsal passed 34 assertions with outbound networking
blocked and no live credentials in its configuration. It includes health,
Administrator access, Pending Access denial, ordinary roles, revocation,
logout/relogin, Front Desk, scheduling, Job Details, gallery metadata and Session
Plan. This does not verify real AD/Entra/social providers or production readiness.
Private installation details and protected backup evidence remain local.

## Isolated Demo runtime and reviewed target

A Demo host using mock sign-in also requires
`Demo:IsolateExternalIntegrations=true`. Production mock sign-in remains
prohibited. The Demo policy pauses external delivery/ingestion, public-site and
WorkDay exchanges, webhooks, accounting export and control-plane publishing.
It retains protected credential values, database/storage configuration and queued
work. Request-driven named clients and the Public Site client also fail before
transport. Internal Studio work and its selected Demo database/storage continue.
No optional AD/Entra provider is enabled by this profile.

`maintenance version` reports the package informational version without loading
application configuration or secrets. The bootstrap command optionally accepts
`--expected-database REVIEWED_JSON`; this nonsecret identity contains `name`,
`schema`, `serverAddress` and `port`. It must match the resolved PostgreSQL
connection before any account/schema writes. Plan/apply output reports that
identity without a connection string or credential. The reviewed deployment hook
always supplies this guard. PostgreSQL addresses are normalized without network
masks, and a real PostgreSQL regression covers that comparison.

The complete pinned solution passes 486 Web and 3 Domain tests locally, without
failures/skips, including the Demo-isolation and database-identity regressions.
The connected restored-database rehearsal verifies both real administrator
applies before startup and rejection of a deliberately different database before
security-table/account writes. Service-manager calls are simulated on the Mac;
actual Linux systemd execution remains part of the authorized cutover.
