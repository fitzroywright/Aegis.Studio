# Unified messaging implementation handoff — October 11, 2026

This is an incremental implementation for local review and validation. Builds, tests, deployment and a real Microsoft 365 exchange have **not** been performed. The October 13 demonstration remains outstanding. Custom fan-out is disabled by default and is not production-validated.

## Ownership and reuse

Studio remains authoritative for conversations. The router writes new unified messages into the existing in-app thread/message tables; it does not create a competing conversation store. The inbox also lists legacy workspace/client conversations without migrating their histories. Existing outbox, polling worker, attachment storage, access services and unresolved-email store are reused.

The address book derives employees, profiles, clients, workspaces and jobs from Studio's existing records. Only route overrides and external contacts require new persistence. A client keeps its existing identity across jobs; having an address does not create an internal inbox. Router classes remain in Studio for the initial release. Common.Messaging supplies transport and provider-independent group discovery contracts; standalone extraction and Google Workspace are deferred.

## Modified components

- Address book and administrator route configuration, including explicit internal/external/combined policies, professional sender verification and provider references.
- Canonical sends, request fingerprints, attachment persistence, recipient planning, delivery ledger and controlled recovery. Internal-only recipients never invoke external transport. Overlapping fan-out recipients are deduplicated case-insensitively.
- Existing outbound queue/worker: stable RFC message IDs, MIME headers, authorized mailbox selection, attachments and attention states. Provider acceptance is distinguished from confirmed delivery. An expired send lease or uncertain provider response is held instead of automatically replayed.
- Existing inbound poller/router: standard reply-header correlation, provider-ID migration compatibility, original-address resolution, registered group routing, attachment/header preservation, receipts and loop suppression. Unknown or unsupported destinations remain in the unresolved queue.
- Messages page: one paged inbox query, server-side search/job filtering, reader below the list, compose with multiple recipients/files, unified address book, professional sender and delivery details/retry controls. Changes use the owning JavaScript/CSS implementations and existing compact card conventions. Legacy conversation controls remain accessible.
- Access enforcement: personal threads require explicit membership, including for administrators; scoped threads and legacy conversations follow Studio workspace/job permissions. External self-sending in a scoped thread also requires SendAsSelf.
- New local regression cases for planning, persistence, MIME/Graph handling, discovery and Messages UI behavior.

## Local configuration and setup

1. Apply both patches to the stated base versions or integrate the changed source files into the current local branches. Review schema changes and back up the local database before starting the modified application. Schema additions execute through the normal application startup path; old conversations are retained.
2. Keep the existing Common.Messaging project reference arrangement. Resolve Studio's CommonRoot to the local Common checkout through the existing Directory.Build.props settings if necessary.
3. Configure the existing Messaging:MicrosoftGraph:Email settings (Enabled, tenant/client credentials and SenderUpn). SenderUpn must be the actual mailbox polled by the router. Configure the existing mailbox polling worker as usual. Keep credentials outside committed configuration.
4. Open Messages → Address book as an administrator. Register each professional staff identity with its real SenderMailbox, professional EmailAddress, SenderVerified and VerificationEvidence. The sending endpoint is an actual mailbox, not a distribution group. The MIME From may be a group identity only after Exchange Send As has been granted and verified in the real tenant. Do not set verification flags based on assumed permissions.
5. Register destinations deliberately. For combined conventional mailboxes choose InternalOnly, ExternalCopy or Hold. ExternalCopy is explicit duplication across the user's two destinations, not a second Studio message. Leave unregistered organizational groups to normal Microsoft 365 distribution.
6. For a Studio-managed static group, use the provider's object ID, register its address, set StudioManagedGroup and record actual membership/permissions validation. Only then set GroupVerified. Leave Messaging:Router:SelectiveFanOutEnabled=false until the tenant tests below pass.
7. Grant the Graph application the tenant-approved mailbox send/read and group/member discovery permissions it requires. Review Exchange restrictions, forwarding and send permissions with the provider administrator. The implementation does not provision mailboxes, groups, permissions or a new email administration system.

## Local validation commands

Run from complete local repositories with dependencies restored; this package is a patch, not a standalone sparse checkout. Use the repository's normal build/test commands in addition to the focused cases:

```sh
dotnet build Aegis.Studio.slnx
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --filter FullyQualifiedName~UnifiedMessaging
node --test tests/ui/unified-messaging.test.cjs
```

From Common.Messaging run its solution build and unit tests, including MicrosoftGraphRoutedEmailTests and MicrosoftGraphRecipientDirectoryTests. Install existing JavaScript test dependencies through the repository's normal workflow. Set STUDIO_TEST_POSTGRES to a **disposable local test database** to run UnifiedMessagingPersistenceTests; they create and drop a random schema and use temporary local attachment storage. Without that setting the existing StudioPostgresFact opt-in mechanism skips persistence tests. Do not treat skipped tests as acceptance evidence.

The only executed checks here were JavaScript syntax, patch whitespace, JSON/XML parsing and packaging integrity checks. No build or test suite has run. Compilation, migration compatibility and browser behavior remain subject to local validation.

## Required Microsoft 365 demonstration

Record the tenant, configuration, permissions, observed provider IDs/message trace and outcomes without committing credentials:

1. Paul → Tamara internal-only: one message, no outbox row and no provider send.
2. Paul → client: verified professional From, monitored Reply-To, attachment intact. Client replies; Paul sees one reply in the originating conversation. Repeat inbound processing and verify both message and attachment counts remain unchanged.
3. Registered group receives an outside message: verify original To/Cc headers reach the monitored mailbox, route only to the registered person/workspace/job, and preserve attachments/sender/headers. BCC, forwarding and recipient-address rewriting require separate tenant proof and must remain unresolved if original intended destinations cannot be established.
4. Enable fan-out only for a tested static group containing the monitor. Send internally first; remaining eligible members each receive one external copy and the monitor receives none. Include overlapping groups, multiple destinations and repeat requests/routing attempts.
5. Test a rejection, temporary known failure, lost response after provider acceptance and expired send lease. Only uncertain outcomes require sent-items/message-trace reconciliation before manual retry. Confirm failed deliveries remain visible and authorized users can recover them.
6. Verify ordinary organizational lists retain normal distribution behavior. Nested, dynamic, hidden, inaccessible or incomplete memberships are controlled failures. Test Exchange delivery restrictions and forwarding rules before enabling a particular group. Discovery cannot prove those policies automatically.
7. Confirm unrelated staff and workspace members cannot read a personal thread. Confirm job reassignment preserves authorized history access.

## Known limits and outstanding issues

- Real-tenant Send As (especially group From identities), MIME From preservation, original-recipient headers, discovery permissions, forwarding/restrictions, duplicate handling and the complete October 13 exchange remain unverified. VerificationEvidence is administrator attestation, not automatic permission proof.
- Only supported mail-enabled static groups with complete, visible, direct membership can be expanded. Nested/dynamic groups and members with missing routable email addresses are held. Discovery caches membership briefly; it does not reproduce provider memberships in Studio.
- Graph sendMail HTTP 202 means accepted, not delivered. Stable RFC identifiers aid correlation/reconciliation but do not provide provider-side exactly-once sends. Outcome uncertainty is held for controlled handling. Delivery confirmations/NDR interpretation and automatic permission discovery are not implemented.
- Attachments are limited to ten files totaling 2.5 MB per composed message; large-attachment upload sessions are deferred. Interrupted uploads must be resumed with the original request and files. A routing retry cannot supply missing attachment data.
- Recipients are snapshotted once external deliveries are queued. A later routing retry cannot silently change a partially sent group's membership. Deliberate new destinations require a new message.
- Unresolved inbound items retain attachment content. Administrator diagnostics/reprocessing are exposed at /api/admin/messaging/unresolved and /api/admin/messaging/unresolved/{id}/reprocess; building a dedicated exception dashboard is deferred. Existing unresolved diagnostics remain available.
- Existing legacy internal histories remain legacy records. Only new unified sends use the new delivery ledger. Legacy Message & Announcement presentation and workflows are preserved.
- Final end-to-end acceptance, forwarding/loop tenant behavior, load/concurrency and migration verification are still required. Do not label selective fan-out production-ready before those checks pass.

## Provider references

- [Microsoft Graph sendMail](https://learn.microsoft.com/en-us/graph/api/user-sendmail?view=graph-rest-1.0)
- [Sending MIME messages](https://learn.microsoft.com/en-us/graph/outlook-send-mime-message)
- [Group overview and unsupported dynamic distribution groups](https://learn.microsoft.com/en-us/graph/api/resources/groups-overview?view=graph-rest-1.0)
- [List group members](https://learn.microsoft.com/en-us/graph/api/group-list-members?view=graph-rest-1.0)
- [Send Outlook messages from another user](https://learn.microsoft.com/en-us/graph/outlook-send-mail-from-other-user)

Source changes were prepared locally. The user subsequently explicitly authorized placing this implementation on main in Aegis.Studio and Common.Messaging so they can pull, build and test. Builds, tests, deployment and real Microsoft 365 validation remain outstanding.


## Resumed checkpoint

Recovered the complete saved Studio and Common.Messaging changes and continued the final recovery review. Fixed route settings so readable option labels retain the server's canonical values. Incomplete attachment deliveries now show the original-file recovery instructions without offering a routing-only retry, and their stale attention entry is cleared transactionally once the original attachment upload completes. Added local UI regression cases for incomplete attachments and known-rejection retries. JavaScript syntax and patch whitespace checks passed; builds, test suites, deployment and real Microsoft 365 validation remain local and outstanding. This updated package supersedes the earlier ZIP.
