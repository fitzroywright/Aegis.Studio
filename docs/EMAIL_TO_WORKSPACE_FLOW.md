# Aegis Studio Email to Workspace Flow

## Objective

Every inbound client email must follow one deterministic path:

`External sender -> Bryan's public/specialty address -> Aegis inbound provider -> InboundMailWorker -> InboundMailRouter -> Workspace -> Conversation -> optional Booking/Job link -> staff UI`

## Routing contract

The original recipient address is the authoritative workspace routing key.

Examples:

- `weddings@bryans.com` -> Weddings workspace
- `graduations@bryans.com` -> Graduations workspace
- `idcards@bryans.com` -> ID Cards workspace
- `production@bryans.com` -> Production workspace

The sender address does not choose the workspace. The sender identifies the client. The original recipient identifies the team/workspace.

## Execution sequence

1. A client sends an email to a Bryan's public or specialty address.
2. The mail platform delivers or forwards a copy to the mailbox monitored by Aegis Studio.
3. `IInboundMailProvider.GetUnreadAsync` returns the unread message with:
   - provider message ID
   - sender address
   - original recipient address
   - subject
   - body
   - Internet Message ID
4. `InboundMailWorker` receives the message and passes it to `InboundMailRouter`.
5. `InboundMailRouter` requires `OriginalRecipientAddress` and resolves that address through `ICommunicationRepository.GetWorkspaceByRecipientAsync`.
6. If there is no configured workspace for that address, routing fails and the message is deliberately left unprocessed so it can be retried after configuration is corrected.
7. If the workspace exists, the router passes the message to `CommunicationService.RouteInboundAsync`.
8. `CommunicationService` normalizes the subject, searches for an existing active conversation in that workspace, and searches for a matching booking using either a booking reference in the subject or the client's active booking in that workspace.
9. If a matching conversation exists, the message is appended to it. Otherwise a new conversation is created in the resolved workspace.
10. The inbound message is persisted under that conversation.
11. The transaction is committed.
12. The router returns a structured result containing workspace, conversation and booking identifiers.
13. Only after successful routing does `InboundMailWorker` call `MarkProcessedAsync` on the provider.
14. Studio logs the complete route: provider message ID, client email, original recipient, workspace, conversation and booking.
15. Staff assigned to or permitted to view that workspace see the conversation in Studio.

## Failure behavior

A message must never be marked processed unless routing and persistence succeed.

Typical failures:

- missing original recipient address
- no workspace configured for the recipient
- database failure
- malformed message

On failure the worker logs the error and leaves the source message unread/unprocessed for retry.

## Important distinction

Email does not create the workspace and email is not the definition of a Job. A workspace exists independently. Jobs can be created manually. Email is one inbound channel that can create or append a conversation and can later be associated with a customer, booking or job.

## Production mailbox integration still required

The current repository still uses `NoOpInboundMailProvider`, so the routing pipeline is implemented but no real Microsoft 365/Exchange mailbox is being read yet. The production adapter should replace that provider without changing the router or workspace logic.

The preferred production arrangement is:

`weddings@bryans.com / graduations@bryans.com / etc. -> AegisHub@bryans.com -> Microsoft Graph/Exchange adapter -> Studio routing pipeline`

The adapter must preserve the original recipient address so Studio can resolve the correct workspace.
