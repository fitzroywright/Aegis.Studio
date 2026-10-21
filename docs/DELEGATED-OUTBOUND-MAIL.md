# Connect Studio's outbound hub mailbox

Studio can send workspace/person mail through the hub's delegated Microsoft identity.
Inbound Graph polling keeps its existing application credentials. No mailbox password
is stored. The connection is application-wide and only a Studio Administrator can
start or disconnect it. The OAuth callback is browser-bound using a single-use,
ten-minute state, a Secure/HttpOnly correlation cookie and PKCE.

## Entra configuration

Use the same tenant-specific app registration as Studio's existing Graph configuration.
Under **Authentication → Add a platform → Web**, register exactly:

```text
https://studio.bryanstudiosltd.com/oauth/outbound-mail/callback
```

Use a Web redirect, not SPA. Under Microsoft Graph **Delegated permissions**, add
`User.Read`, `Mail.Send.Shared`, and `Mail.Read`, and grant administrator consent.
The authorization request also includes `offline_access` for refresh tokens.
Keep the existing application permissions for inbound polling and notifications.
Keep the hub's verified Send As grants on the workspace/person distribution lists.
Conditional Access may require periodic interactive reconnection.

## Server configuration

Set these environment entries in Studio's existing service environment configuration:

```text
Messaging__MicrosoftGraph__Email__Delegated__Enabled=true
Messaging__MicrosoftGraph__Email__Delegated__RedirectUri=https://studio.bryanstudiosltd.com/oauth/outbound-mail/callback
Messaging__MicrosoftGraph__Email__Delegated__CacheDirectory=/var/lib/aegis/studio/outbound-oauth
Messaging__MicrosoftGraph__Email__Delegated__EncryptionKeySecretName=messaging/msgraph/outbound-cache-key
```

Retain `Messaging__MicrosoftGraph__Email__SenderUpn=aegishub@bryanstudiosltd.com`,
the existing tenant GUID, client ID, client-secret name, and Email Enabled setting.
Use the same canonical host in the browser and RedirectUri so the correlation cookie
returns to the right host. Nginx must forward this callback to Studio.

Create a **new persistent** secret named `messaging/msgraph/outbound-cache-key` in
the Common.Secrets provider already used by Studio (for example OpenBao). Its value
must be 32 cryptographically random bytes encoded as base64. On the Mac, generate
the value for entry into your secret manager with:

```bash
openssl rand -base64 32
```

Do not put this key in source control, chat, or the token-cache directory. Grant the
Studio service identity read access through its existing secret-provider policy.
Do not replace the Graph client secret. Keep and back up the encryption key securely;
changing or losing it requires disconnecting/reconnecting the mailbox.

Ensure `/var/lib/aegis/studio` is writable by Studio's existing service account.
The application creates the dedicated cache directory with owner-only permissions,
writes its AES-256-GCM encrypted cache with mode 0600, and atomically replaces it.
It survives service restarts and deployments. This file cache supports **one Studio
process**; do not share it between multiple simultaneous instances.

Prevent reverse-proxy/access logs and telemetry from capturing query strings on
`/oauth/outbound-mail/callback`: the callback contains a short-lived authorization
code. Studio does not log token endpoint bodies or return access/refresh tokens.

## Mac validation and deployment

Pull Studio and keep Common.Messaging current. Run locally from the Studio checkout:

```bash
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --configuration Release --filter 'FullyQualifiedName~StudioDelegatedGraphTests|FullyQualifiedName~StudioGraphMailboxTests|FullyQualifiedName~EmailAssociationTests.CommonMessagingMimeAndProviderReadBackUseActualInternetId'
```

Then use your existing Mac build/deploy script, which runs the full required checks.
No build, restore, tests, or live Microsoft mail submission was run in the agent's
environment during this change.

## Authorize and verify

1. Sign into Studio as an Administrator and open
   `https://studio.bryanstudiosltd.com/admin/outbound-mail`.
2. Click **Connect outbound mailbox**. Sign in to Microsoft as
   `aegishub@bryanstudiosltd.com` and accept the approved permissions.
3. Studio verifies `/me` matches the configured hub before saving encrypted tokens.
   A different Microsoft account is rejected. The page then shows **Connected**.
4. Retry **one** rejected reply in Studio. Do not bulk replay earlier attempts.
5. On Kratos, watch:

```bash
sudo journalctl -u aegis-studio -f -o short-iso-precise \
  | grep --line-buffered -Ei 'Outbound|Graph.*send|threading verification'
```

Expected: attempt → provider accepted → acceptance persisted → threading verification
completed. Provider acceptance means submission, not confirmed recipient delivery.
Check the recipient inbox and reply routing as the separate end-to-end proof.

The outbox uses a delegated token with `/me/sendMail`; the existing MIME builder keeps
From, Reply-To, references, attachments and correlation headers. Sent Items verification
uses `/me/mailFolders/sentitems/messages`. Acceptance is persisted before verification,
so delayed read-back never schedules a resend. A timeout or server failure after
submission is held for reconciliation. Revoked/missing authorization holds deliveries
for administrator attention; there is no silent fallback to app-only authentication.

## Reconnect or roll back

The administrator page's **Disconnect** action deletes Studio's local token cache
and invalidates pending connections; it does not revoke tenant consent or Microsoft
sessions. Reconnect to authorize again. Previously held/rejected deliveries require
an explicit retry; already accepted deliveries must not be resent.

To restore the previous application-token transport, set
`Messaging__MicrosoftGraph__Email__Delegated__Enabled=false` and restart Studio.
This does not cure its previous distribution-list SendAsDenied error. Inbound polling
and other existing Common.Messaging channels are unaffected by this mode switch.
