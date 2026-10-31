# Studio sign-in provider

The login page has one **Sign in** button. Provider selection belongs to server
configuration; users do not choose a login type.

Set `Authentication:Provider` (environment variable `Authentication__Provider`) to:

| Value | Behavior | Required configuration |
| --- | --- | --- |
| `ActiveDirectory` | Username/password validated by the AD plugin | `AuthenticationPlugin:Enabled=true` and working plugin/directory settings |
| `LocalDatabase` | Username/password validated against enrolled local accounts | Existing local credentials and approved Studio access |
| `EntraId` | Sign in redirects to the configured Entra tenant | `Authentication:Entra:Enabled=true`, tenant/client settings, client secret and registered callback |
| `Mock` | Existing allowlisted demo identity | Explicitly isolated Development/Demo configuration; prohibited elsewhere |

When `Authentication:Provider` is absent, existing configuration is resolved in
this order: permitted mock, enabled Entra, enabled AD plugin, otherwise local.
The AD plugin's existing default is enabled. Use an explicit provider when more
than one integration is configured. An unknown or unavailable provider fails
startup rather than falling back to a different credential store.

The server authenticates against exactly the selected provider. The client no
longer sends a provider choice. Legacy requests naming a different provider are
rejected. The Entra challenge route is available only when Entra is selected.
Failed credentials never cause another provider to be tried.

Local recovery is shown only for local sign-in. Demo provider labels are omitted
from the login page; the existing environment and identity restrictions remain.
Account approval, role assignment, immutable provider bindings and cookie
validation continue to apply to every provider.

Changing configuration does not migrate identity bindings or grant application
access. Follow the existing cutover procedure and deploy the complete Studio
bundle to apply this source change.

Studio mock sign-in is password-free. The password field is hidden and a legacy
`MockAuthentication:Password` value is ignored. Only the server allowlisted identity
is accepted, and isolation, account approval and role restrictions still apply.


## Administrative access bootstrap

The built-in `Administrator` access role is required. Studio creates it on the
first run and repairs it after startup or a demo/factory reset if it was retired
or lost unrestricted access.

Set `Authentication:AdministrativeUsers` to the approved Studio login aliases
that must receive administrative access. The same setting applies to Active
Directory and Microsoft Entra ID; use the alias stored on the person's Studio
account. For example:

```json
{
  "Authentication": {
    "AdministrativeUsers": [
      "DOMAIN\\studio.admin",
      "studio.admin@example.com"
    ]
  }
}
```

Configured entries are assigned idempotently on every startup and after a
factory reset. They still need an approved Common.Security account and a valid
AD or Entra identity binding before they can sign in. AD
`AuthenticationPlugin:Settings:AdministrativeGroups` continue to grant the
same role through verified directory-group membership.

## Manage demo staff through Staff & Roles

When Studio is running in the explicitly isolated `Demo` environment with Mock
sign-in selected, **Demo (no password)** is available when adding staff or setting
up sign-in for an existing staff record.

For a directory-only person such as a seeded photographer:

1. Select the person in **Staff & Roles**.
2. Choose **Set up sign-in**, select **Demo (no password)** and create the account.
3. Select the person again, choose the appropriate access roles, set **Account
   status** to **Active**, and save.
4. Sign in using their existing user name and no password.

For a person who already has an authentication account, **Enable demo sign-in**
adds a demo binding to that exact account. It preserves the account ID, alias,
other authentication bindings, approval status and roles. Disabled or suspended
accounts require a separate status review first. Repeating enrollment preserves
the existing demo subject.

The administrator-only enrollment endpoint is
`POST /api/admin/security/demo-accounts/`. It requires a current administrator
session and rejects cross-origin browser mutations. Its request contains
`userName`, `displayName` and, when adding a binding to an existing account,
the explicit `accountId`. Subject IDs are assigned by the server, never the browser.

Managed demo identities are durable bindings in the existing application-scoped
account database; they do not require editing a JSON allowlist or restarting
Studio. Configured operator identities remain supported and keep their original
subject IDs. New accounts are Pending Access and receive no default roles.
Removing the managed identity or disabling/suspending its account prevents new
demo sign-ins.

Enrollment and these UI controls are unavailable in Production, Staging,
Development, non-isolated installations, or when a real authentication provider
is selected. Common.Security and the other Aegis applications are unchanged.

