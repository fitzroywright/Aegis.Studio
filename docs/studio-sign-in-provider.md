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
