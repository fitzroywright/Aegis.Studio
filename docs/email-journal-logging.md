# Studio email journal logging

Receipt is logged after a Graph message is retrieved and mapped, before intake/routing.
Receipt does not mean that routing succeeded. Each retrieved occurrence has a receipt
event; Graph checkpoint retries may retrieve the same message again. WasDuplicate in
the routing result distinguishes an intake duplicate from a newly imported message.
An exception leaves the checkpoint unchanged, preserving the existing retry behavior.

Inbound receipt, routing result and routing exception events contain MessageId,
ProviderMessageId and CorrelationId (the retrieved Internet Message-ID). Routing
results link these to ConversationId or ReviewId, and include WasDuplicate.

Outbound attempts, submissions, provider acceptance and failures contain DeliveryId,
ConversationId, StudioMessageId (when applicable) and Attempt. DeliveryId is also
the provider request CorrelationId. Follow ConversationId from an inbound routing
result to outbound attempts; use DeliveryId to follow a particular send and retry.
Submission includes the proposed Internet Message-ID and InReplyTo; threading
verification records the actual provider ID and verified Internet Message-ID.
Provider acceptance means the provider accepted the request, not inbox delivery.
Acceptance is persisted before threading verification; a verification failure
retains acceptance and does not schedule another send.

Identifiers are explicit template fields, so default console journal output does
not depend on logging scopes. These fields are in the journal MESSAGE text; they
are not native journald indexed fields. Keep Information logging enabled for
Aegis.Studio.Web.Email. Bodies, headers, attachment names/contents and credentials
are excluded. Inbound sender, subject, received timestamp and attachment count are
intentionally logged. Worker failures record exception type and, for definitive
HTTP rejection, status code, without dumping arbitrary exception/provider payloads.

## macOS verification

Run in the existing Aegis.Studio repository on the Mac, using .NET 10 and the
existing sibling Common checkout (or an explicitly configured CommonRoot).

```zsh
git fetch origin
git switch --track origin/fix/email-receipt-journal
dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release --no-restore
read -rs 'STUDIO_TEST_POSTGRES?Disposable test PostgreSQL connection string: '
export STUDIO_TEST_POSTGRES
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~EmailLoggingTests|FullyQualifiedName~StudioDelegatedGraphTests|FullyQualifiedName~IntakeCompletionTests|FullyQualifiedName~OutboundDeliveryLeasePersistenceTests' --logger 'console;verbosity=normal'
```

Use a test PostgreSQL database with permission to create/drop schemas. Never use
production credentials. The fixtures create isolated random schemas and remove
them afterward. Four of the eight new logging tests require PostgreSQL; they must
run successfully, rather than skip because STUDIO_TEST_POSTGRES is absent.
The other four directly exercise receipt/routing with an in-memory logger.
Outbound tests exercise the worker and real outbox persistence with a fake email
provider: acceptance followed by unavailable threading verification, HTTP 403
rejection, retryable failure and uncertain outcome. Existing delegated Graph tests
cover mocked Graph responses without making external sends.

Verification status at preparation: diff whitespace, XML parsing, log placeholder
argument counts/unique keys, payload exclusion and acceptance ordering inspected.
The C# build and tests have not been run. Keep the PR draft and unmerged until
the Mac commands above pass, including the PostgreSQL cases.

## Kratos journal

Run on Kratos after deploying the verified build.

```bash
sudo journalctl -u aegis-studio --since "15 minutes ago" --no-pager -o short-iso-precise --grep='Inbound email (received|routing)|Outbound |Submitting outbound delivery'
sudo journalctl -u aegis-studio -f -o short-iso-precise --grep='Inbound email (received|routing)|Outbound |Submitting outbound delivery'
sudo journalctl -u aegis-studio --since "1 hour ago" --no-pager -o short-iso-precise --grep='Inbound email routing failed|Outbound provider rejected|Outbound provider outcome is uncertain|Outbound delivery attempt failed|Outbound accepted delivery needs threading verification|email ingestion cycle failed|Outbound message processing cycle failed'
```

To trace a specific message, paste its MessageId, ProviderMessageId, ConversationId
or DeliveryId at the prompt. Fixed-string matching avoids treating IDs as regexes.
Use a ConversationId to connect inbound and outbound paths, then a DeliveryId to
isolate a send attempt and its acceptance.

```bash
read -r -p 'Message, provider, conversation or delivery ID: ' trace_id
sudo journalctl -u aegis-studio --since "24 hours ago" --no-pager -o short-iso-precise | grep -F -- "$trace_id"
```
