# Email logging verification

This change is based on current main, replacing only the remaining logging gap from stale PR #221.
Build and test execution belongs on the Mac. A successful source review does not establish that the tests pass.

## macOS: build and run the focused tests

From the existing Aegis.Studio checkout (with its existing sibling Common repositories):

```bash
cd /Users/fitzroyf.wright/Projects/Aegis.Studio
git fetch origin
git switch --track origin/fix/email-receipt-tracing-20261023
dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release --no-restore
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~EmailWorkerLoggingTests|FullyQualifiedName~EmailAssociationTests|FullyQualifiedName~StudioDelegatedGraphTests|FullyQualifiedName~OutboundDeliveryLeasePersistenceTests|FullyQualifiedName~IntakeCompletionTests' --logger 'console;verbosity=normal'
```

If the branch already exists locally, use `git switch fix/email-receipt-tracing-20261023` followed by `git pull --ff-only` instead of `git switch --track`.

Four new inbound logging tests run without a database. Three outbound worker tests use the existing isolated PostgreSQL fixture. To run the database-backed tests, set `STUDIO_TEST_POSTGRES` to a dedicated test database connection string with permission to create/drop isolated schemas. Do not point this at production.
If the variable is absent, database tests skip; a green run with skipped tests is not verification of provider acceptance or rejection.

To enter the dedicated test connection string privately in macOS zsh:

```zsh
read -rs 'STUDIO_TEST_POSTGRES?Dedicated test PostgreSQL connection string: '
export STUDIO_TEST_POSTGRES
echo
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~EmailWorkerLoggingTests|FullyQualifiedName~EmailAssociationTests|FullyQualifiedName~StudioDelegatedGraphTests|FullyQualifiedName~OutboundDeliveryLeasePersistenceTests|FullyQualifiedName~IntakeCompletionTests' --logger 'console;verbosity=normal'
unset STUDIO_TEST_POSTGRES
```

Expected coverage: receipt before intake; routing failure with receipt retained and exception propagated; held/duplicate routing results; shutdown cancellation; one provider acceptance retained after threading verification failure; definitive rejection held for attention; one retry failure event. Provider calls are fakes and do not send live mail.

After the local checks pass, merge this correction into main. PR #221 itself remains superseded; its stale branch must not be merged wholesale.

## Kratos journal checks after normal deployment

Run on Kratos:

```bash
sudo journalctl -u aegis-studio --since '15 minutes ago' --no-pager -o short-iso-precise \
  | grep -E 'Inbound email (received|routing)|Outbound delivery attempt|Submitting outbound delivery|Outbound provider|Outbound acceptance persisted|Outbound threading|Outbound accepted delivery|Outbound mailbox'

sudo journalctl -u aegis-studio -f -o short-iso-precise \
  | grep --line-buffered -E 'Inbound email|Outbound|Submitting outbound delivery'
```

Trace an exact inbound MessageId or ProviderMessageId, an outbound DeliveryId, or the verified InternetMessageId:

```bash
TRACE_ID='paste-the-exact-identifier-here'
sudo journalctl -u aegis-studio --since '1 hour ago' --no-pager -o short-iso-precise \
  | grep -F -- "$TRACE_ID"
```

Inbound event IDs: 22101 receipt, 22102 routing result, 22103 routing failure.
Outbound event IDs: 22110 attempt, 22111 submission, 22112 provider acceptance, 22113 rejection, 22114 retryable/exhausted failure.

Inbound CorrelationId is the retrieved MessageId. Routing results link it to ConversationId/ReviewId and WorkspaceId where available.
Outbound CorrelationId is DeliveryId. The attempt links DeliveryId to ConversationId and StudioMessageId; submission records the proposed InternetMessageId; successful threading verification records the actual InternetMessageId and ProviderMessageId.
Search a ConversationId to find its delivery attempts, then search a DeliveryId for the rest of that delivery.

Provider acceptance means the transport accepted the send request; it does not prove delivery to the recipient inbox.
Retrieval retries may produce another receipt observation for the same MessageId. WasDuplicate in the routing result distinguishes existing intake records; there is one receipt event per processing attempt.
Routing exceptions abort the cycle without advancing the delta checkpoint. Shutdown cancellation is not logged as a routing failure.

The receipt records sender, subject, received time and attachment count. Bodies, attachment names/bytes, raw exception text and credentials are excluded from these worker logs.
