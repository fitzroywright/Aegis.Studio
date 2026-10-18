# Client association and email routing

## Result

Workspace email creates an individual inquiry (`studio_conversations`), not the workspace's shared team thread. Equal sender, subject, or provider conversation ID does not join fresh inquiries. The original inquiry ID remains the job conversation ID on promotion; its client selection, messages, received times and attachments remain intact.

`StudioEmailIntake` is the entry point for the Microsoft 365 worker, administrator inbound API, legacy IMAP importer, unresolved-mail reprocessing and staff routing confirmation. `StudioEmailRouting` evaluates recorded provider-verified reply identifiers and explicit AEGIS references before workspace fallback. The old sender/subject/provider-thread fallback and separate short-reference recovery writer are removed.

Immediate In-Reply-To is preferred when recorded. Other recorded ancestors and explicit references must agree; conflicting signals are held. Locally proposed but unverified outbound identifiers are visible as a verification hold rather than trusted. Valid references from unfamiliar senders show **Reference matched—unrecognized sender**. No acknowledgement, reply, access grant or contact-email update is triggered by matching or review.

## Clients and staff actions

Sender addresses use the same normalization for client matching and routing checks: parse an address, trim and compare without case. SQL applies equivalent normalization to persisted addresses, including display-name wrappers. Provider-specific aliases, plus tags and dots are not rewritten. Exactly one client matches automatically; zero/multiple matches remain unassigned. The inquiry panel shows matching candidates and lets authorized staff search, assign, change or remove its client. Removal is explicit and locked against later automatic matching. Promotion copies the selected client ID and name into the job.

Messages and workspace inquiry views include the association panel, history, job creation/linking, deliberate alternate contacts and received-email moves. The Messages toolbar opens Email routing review. Staff can confirm an intended inquiry/job or create separate work. Alternate contacts are deliberately scoped to an inquiry/job; they are not account memberships or permission grants.

Before promotion, changes require workspace ReplyRequester; job creation retains CreateJob. After promotion, association changes require job EditJob plus ReplyRequester. Reviews require access to every referenced source and the destination. Shared workspace threads require workspace reply permission, job threads job editing/reply permission, and direct threads explicit membership. Existing send identity authorization remains enforced.

A job's original inquiry cannot be detached into another job while leaving the original job pointing at it. Use Move received email to split the new work instead. Secondary inquiries may be linked/unlinked. Splits retain the message ID, received timestamp, external ID and attachment IDs, and record the old/new association and actor. Historical attachments with no per-message ownership block a split until ownership is reconciled; they are never guessed or silently left behind.

## Identity, retries and history

New public references use the complete inquiry UUID (`AEGIS-<32 hex digits>`); thread references use a separate `AEGIS-T<32 hex digits>` namespace. They survive promotion. Previously issued eight-digit references remain supported only when unambiguous across all matching inquiries; closed inquiries are not silently excluded. A reference is a routing hint, not an authorization token.

Outgoing email subjects and bodies consistently carry the reference and a short instruction for new email about the same work. Replies include In-Reply-To/References where an inbound identifier is available. Workspace and approved personal identities continue through the existing queue, with a monitored Reply-To required.

Intake serializes by provider/Internet Message-ID across entry points. Durable receipts preserve provider IDs and headers, hash the sender/body/subject/attachments, and distinguish incomplete attachments from completed imports. An interrupted attachment upload resumes using existing storage source keys. Conflicting reuse of a message identifier is held. Staff corrections are authoritative on reprocessing. Session locks are released before pooled connections are returned. Audit rows include actor, time and previous/new values.

## Microsoft 365 verification

Common.Messaging's current Graph channel sends MIME with Message-ID, From, Reply-To and X-Studio-Message-Id. Its `SendAsync` returns no provider message identifier. Studio therefore reads the saved sent item, matches the correlation header, selected From, recipient and monitored Reply-To, and records **the returned actual Internet Message-ID** and provider item ID. Only verified identifiers are trusted for reply routing. If read-back is delayed, ambiguous, denied or inconsistent, delivery is held for reconciliation; it is not automatically resent.

Administrator support can POST `/api/email/outbox/{deliveryId}/verify-provider-id` with `{ "sentAfterUtc": "..." }` to reconcile a submitted/held email without sending it. The optional time starts a bounded 15-minute sent-item search window; default is five minutes before provider acceptance/creation. This also supports historical MIME sends. Older sends without a recoverable correlation header need manual reconciliation. A locally stored historical identifier is not marked verified by migration.

**No live Microsoft 365 send/reply round trip has been run.** Common.Messaging/Microsoft Graph HTTP interactions were exercised with a fake handler, including a provider-returned identifier different from the proposed identifier. Provider-level reliability remains unverified until a controlled, explicitly authorized live round trip confirms both selected sending identities, monitored Reply-To, saved headers and replies through the real ingestion worker.

## Deployment and preservation

- `Email/EmailAssociations.sql` is an embedded, additive startup migration, run after clients and unified messaging schema initialization. It adds inquiry client links, reference aliases, alternate contacts, history, intake receipts, attachment ownership links and provider-verified outbound identifier history. Repeated migration is supported. No correspondence, attachments or old thread IDs are deleted or automatically repartitioned.
- Historical shared workspace conversations remain intact. Replies referencing them enter review so staff can choose a separate inquiry/job; they do not continue accumulating automatically in the shared thread.
- Deploy all intake entry points together; drain/stop old ingestion workers before enabling the new release. Mixed old/new ingestion can bypass the shared policy.
- Microsoft Graph requires access to read the saved sent items in each authorized sending mailbox, in addition to existing Mail.Send and monitored-mailbox ingestion permissions. Verify Send As/from behavior and the monitored Reply-To in the tenant. Standard Graph references: [get message and headers](https://learn.microsoft.com/en-us/graph/api/message-get?view=graph-rest-1.0), [list messages](https://learn.microsoft.com/en-us/graph/api/user-list-messages?view=graph-rest-1.0).
- The PR also repairs the reminder rule reader syntax/type error found when compiling the freshly merged main. Financial reminder rule filtering remains in the authorized endpoint.

## Local checks

Build: `dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore -m:1 -p:BuildInParallel=false`.

Non-database tests: `dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build -m:1`.

Database acceptance tests: set `STUDIO_TEST_POSTGRES` to an isolated database and run `dotnet test ... --filter 'FullyQualifiedName~EmailAssociationTests|FullyQualifiedName~UnifiedMessagingPersistenceTests'`. Fixtures own random schemas and temporary file roots. They are serialized as one collection. Locally these tests use PostgreSQL via PGlite 0.5.10 and a local protocol adapter; this is functional SQL/service verification, **not native multi-session concurrency verification**. Native PostgreSQL concurrent mailbox/import/reprocess stress remains a deployment check.

UI: `node --test tests/ui/inquiry-association.test.cjs tests/ui/unified-messaging.test.cjs` (jsdom).

Coverage includes client match cardinality, staff removal/assignment, permissions, two inquiries/two jobs, pre-job replies after promotion, explicit references before fallback, unknown senders, conflicting references, local-only identifiers, legacy collisions, historical workspace review, repeat migrations, duplicate provider batches, attachment identity, interrupted uploads, split/reprocessing corrections, escaped UI content and search selection stability.
