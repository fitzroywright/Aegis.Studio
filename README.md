# Aegis.Studio

Aegis.Studio is the operations platform for Bryan's Studio.

The core model is deliberately **not email-first**. External systems and channels are paths into Aegis. A request can arrive by email, phone, walk-in, chat, WhatsApp, portal, or another future channel. Aegis resolves the contact, routes the interaction to a workspace, creates or appends to a conversation, and lets staff decide whether that conversation should be linked to a job.

## Core flow

`Channel -> Workspace -> Contact -> Conversation -> optional Job -> Production -> Financials -> Delivery/Gallery`

A conversation is never destroyed when a job is created. It remains the communication history and is linked to the job. Jobs can also be created directly for walk-ins, telephone work, repeat customers, and internal work.

## Department lenses

The same underlying data is presented differently to each team:

- Front desk / sales: new requests, active conversations, quotes, bookings, deposits.
- Photographers: assigned shoots, call times, locations, shot lists, uploads, expenses.
- Production: work queues, original assets, editing, approvals, print/delivery stages.
- Accounting: deposits, balances, payments, refunds, expenses, reconciliation.
- Management: workload, conversion, revenue, bottlenecks, overdue work, profitability.

## Microsoft 365 mail router

Studio uses one Microsoft 365 collection mailbox for transport. The collection mailbox is infrastructure only and must not be presented as the user-facing source of a Studio message.

Workspace addresses such as `weddings@bryanstudiosltd.com` and logical Person addresses such as `fwright@bryanstudiosltd.com` can be mail-enabled aliases or distribution addresses that deliver to the collection mailbox. A Person address does **not** require an individual Exchange mailbox. Studio resolves that address to the employee's persisted Studio identity; future AD synchronization can create/update the same identity association. Job routing is also supported through `X-Aegis-Route: job:<job-guid>` or the generated `job+<job-guid>@<Studio messaging domain>` address.

Inbound routing order is:

1. Existing Microsoft Graph conversation/thread identity or a persisted `[AEGIS-XXXXXXXX]` conversation reference.
2. `X-Aegis-Route`, when present, for Person, Workspace or Job routes.
3. Original `To` and `Cc` recipients.
4. If exactly one Person, Workspace or Job route matches, route automatically.
5. If none or multiple match, place the message in the unrouted review queue.

Outbound email to ordinary external recipients is sent through Common.Messaging using the configured Microsoft 365 transport. The Studio conversation keeps the logical Person identity and AEGIS reference even though the transport uses the shared Microsoft 365 sender. External replies therefore return to the same Studio conversation rather than creating unrelated threads.

Microsoft Graph configuration must be provided through configuration/environment variables. Do not commit tenant credentials or client secrets to Git.

### Windows PowerShell configuration example

```powershell
$env:MicrosoftGraphEmail__Enabled = "true"
$env:MicrosoftGraphEmail__TenantId = "<tenant-id>"
$env:MicrosoftGraphEmail__ClientId = "<application-client-id>"
$env:MicrosoftGraphEmail__ClientSecret = "<client-secret>"
$env:MicrosoftGraphEmail__Mailbox = "AegisHub@bryans.com"

dotnet run --project src/Aegis.Studio.Web
```

For a persistent Windows service or production deployment, set the same values in the service environment or secret store instead of in source-controlled JSON.

After startup, check:

- `/health`
- `/api/email/status`
- `/api/email/unrouted`

Send a test message to one configured workspace distribution list and confirm that the conversation appears under that workspace. External replies from Aegis are queued for the outbound Graph worker; internal notes never leave Aegis.

## Repository direction

This rebuild starts with the domain and conversation/job boundary. Microsoft 365 is an email transport and identity/integration provider, not the organizing model for the application. Common.Security will be integrated at the application boundary rather than duplicated here.

Development branch: `rebuild/studio-operations-platform`.
