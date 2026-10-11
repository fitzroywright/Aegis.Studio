# Complete workflow implementation — recovery checkpoint

Authoritative specification: `COMPLETE-WORKFLOW-20261011-BRIEF.txt`.
Branch: `feature/studio-complete-workflow-20261011`.
Baseline main: `d12121c99e53e69c227202a0faa2a42d62bd746a`.

## Initial audit (source review, not runtime acceptance)

All 364 text/source files inventoried; workflow, front desk, scheduling, mobile client/server, media/versions, production, galleries, authorization, navigation, existing tests and progress reports inspected. No AGENTS.md exists in the baseline tree. Existing components will be extended at their original sources.

| Brief area | Existing evidence | Gap / next work |
| --- | --- | --- |
| Pending booking/routing | FrontDeskBookingRequests, PublicBookingEndpoints, Unassigned workspace | Preserve intake; required deposit threshold is currently ignored |
| Deposit requests | Front Desk flag endpoint | Actually enqueue messaging; expose provider evidence, distinguish manual receipt |
| Operations decisions | WorkflowStore confirm/reopen, monitor | Dedicated override request/decision queue; reopening must be Operations-only |
| Ownership/scheduling | Connected workflow sessions/crew, ScheduleStore, assignment requests | Owner acceptance; production permission boundary; compatibility writers and concurrent reservations need integration verification |
| WorkDay | MAUI app, SQLite outbox, device tokens, field API | Remove upload/timekeeping from active UI; decline; per-photographer state; completion aggregation; replay facts/locks; offline ordering and truthful acknowledgements |
| Upload/versioning | MediaIngestQueue, asset versions/derivatives, review manifests | Verify storage-backed receipts and thumbnail retry; source contributions remain explicit |
| Production/review | Tasks, commercial orders, immutable review manifests | Job Owner must not acquire Production Manager dispatch/final approval by ownership |
| Gallery/delivery | Gallery proof/access/selection/publication | Review original endpoint and per-job payment/Finance authorization; do not expose unpaid originals |
| Finance | Financial events, Billing, receivables | Finance Manager-specific credit decisions and server release gates |
| Closure | Workflow close/reopen, SQL triggers | Audit all alternate writers and Operations-only reopening |
| Public V1 | Older booking and gallery APIs | Formal /api/public/v1 contract, errors, pagination, auth, idempotency, rate limiting, signed durable webhooks, reference website |
| Navigation/Today UI | Shared shell and page controllers | Audit every role/action against approved workflow; no added CSS overlays |
| Acceptance | Existing .NET and DOM suites | Run locally; add meaningful multi-role/multi-photographer/offline tests; Android/iOS device acceptance remains required |

## Checkpoint 1

Dedicated remote branch created. Authoritative brief and audit checkpoint saved. No implementation merged or deployed. No database/customer/payment data modified.

Build environment: no preinstalled .NET SDK; attempting local SDK installation. Common sibling dependencies are not present. MAUI mobile/platform tools and physical devices are not available. Existing historical reports do not establish current acceptance.

## Recovery

Checkout the branch, read this file and the authoritative brief, inspect commits and compare latest main before resuming. Never merge failing/incomplete changes. Record exact validation commands/results and blockers at every checkpoint. Prioritize WorkDay photographer scope and transaction-safe session reporting, then confirmation/messaging and operational/finance decisions, public contract/webhooks/reference site, connected acceptance.

## Checkpoint 2 — photographer reporting foundation

- Replaced active WorkDay home/detail UI with photographer assignments, accept/decline with reason, arrival, start, finish, notes/problems, availability and synchronization. Removed active uploads, gallery management, general work sign-in/out and timekeeping displays. Preserved existing local data and reusable legacy services; old pending file data blocks logout until recovered.
- Added per-photographer assignment state (additive table; legacy state attributed only to its recorded actor). Shared shoot completion now waits for all currently assigned photographers. Owner and Workspace Manager notifications use the existing transactionally enqueued workflow notification function.
- Serialized operation replay and Job/schedule state; bound operation IDs to reporter/payload; checked current assignment inside the write transaction; made note-operation foreign key deferred to avoid failing note inserts before acknowledgement insertion.
- Added explicit decline, required decline/problem reasons, active-stage and closed-Job guards, required expected versions, device-ID binding and reporting-only device-token route restrictions.
- Offline events retain original IDs/expected versions, chain versions correctly, recover Sending entries, require matching explicit server acknowledgements and block dependent events after conflict. UI shows locally queued status separately from Studio status.
- Corrected configured deposit threshold (including no-deposit-required bookings) and limited reopening to Operations Manager authorization.

Validation: Studio Web compiled with .NET SDK 10.0.401, 0 warnings / 0 errors. The first native suite after photographer changes passed 203 tests, skipped 13 database-dependent tests, 0 failures (216 total). Deposit changes happened after this run and require rerun. Native builds use `-m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false`; parallel MSBuild named pipes are unsupported in this runtime.

Remaining / not accepted: PostgreSQL integration tests (disposable server could not start under a non-root user because process user switching is unsupported); Android/iOS MAUI workload/platform/device verification; conflict resolution UX and reassignment generation handling; queued offline availability; management detail/history visibility; actual deposit messaging; dedicated Operations requests/decisions; Finance credit/releases; formal public V1/webhooks/reference site; complete role/production/navigation/closure acceptance. No merge or deployment.

## Checkpoint 3 — connected deposit requests and Operations decisions

- Deposit-request action now transactionally inserts an email in the existing outbound queue and records the message ID. Repeat clicks reuse that message. It requires a configured Microsoft email channel, workspace sender identity, customer email and positive required deposit. Delivery-status endpoint/UI exposes queue state, attempts, failure and provider acceptance; never claims recipient delivery without a receipt. No live message was sent during implementation.
- Added a dedicated Operations Management page using StudioPage/Today structure, permission-gated navigation, pending override decisions, history, unassigned/overdue/blocked work and direct Job links. Front Desk requests overrides from Pending Job Details. Only an Operations Manager other than the requester may decide. Decisions record actor/reason/time; approval does not record payment or automatically confirm. Confirmation checks either the required deposit or an approved override covering the current required amount.
- Job Details now displays photographer reporting history and booking decision history. Reopening button follows the same Operations authorization as the server.
- WorkDay device tokens take precedence over lingering login cookies and fail closed when invalid; they cannot become general Studio/production/upload sessions. Added eight boundary tests. Offline cache account enrollment is bound to one server/account; revoked credentials do not silently fall back to online authentication. Existing queued data is preserved.
- Corrected WorkDay project desktop-target inheritance and explicit MAUI Controls reference; build script restores Android/iOS workloads separately. Android build reaches NETSDK1147 for missing maui-android; Android/iOS binaries and device acceptance are unverified.
- WorkDay Today is calculated in Studio's configured timezone (default America/Jamaica); assignment authorization uses an ID-bound schedule query rather than scanning twenty years of entries.

Validation so far: the native suite before Operations changes passed 212, skipped 13, 0 failed (225 total); all existing UI tests passed 93/93; focused controller/UI verification passed 10/10. Added Operations self-approval/decision navigation tests and started final native/UI reruns; their final results must be recorded before acceptance. SQL transactions/migrations and external messaging remain unverified without a disposable PostgreSQL service and configured test provider. No main merge, deployment, reset or operational data mutation.

Next: capture final test results; validate schema/transactional WorkDay replay, multi-photographer completion, deposit outbox and override decisions against PostgreSQL; compile/test MAUI on Android/iOS; then implement Finance-specific delivery authorization, production role boundaries, formal public V1/webhooks/reference site and remaining connected acceptance. Existing Finance/gallery bypass risks are not resolved by these checkpoints. Main is not a release candidate for the complete brief.

## Checkpoint 4 — verified recovery handoff

Final native Web/Core suite: **216 passed, 13 database tests skipped, 0 failed**. Final UI controller suite: **94 passed, 0 failed**. Edited JavaScript syntax and whitespace checks pass. Remote implementation checkpoint `ba6ee51775c5f6652e042800d3d8426402e13b5e` verified by reading its branch ref. See `COMPLETE-WORKFLOW-20261011-ACCEPTANCE.md` for verified scope, missing environment/device evidence, incomplete requirements and recovery instructions. The full implementation remains incomplete and is not merged or deployed.

## Resumed implementation — Finance and production boundaries

- Added explicit Finance Manager and Production Manager operational roles (including directory-group resolution). Finance authorization is independent of Operations override and administrator-wide billing access.
- Added auditable JMD final-release authorization with amount, customer snapshot, terms, due date, contract reference, approver and revocation. It preserves actual financial events and receivables, including operationally completed Jobs.
- Delivery and Front Desk fulfillment now use the connected workflow and require actual settlement or sufficient Finance authorization. Front Desk records delivery; Job closure remains a separate workflow decision.
- Production task dispatch/reassignment and final technical approval require Production Manager authority. Job Owner can report progress only on assigned production work. Existing media assignment changes also check the explicit role.
- Legacy workspace gallery public endpoints now serve reduced, visibly watermarked proofs only. Those galleries lack a pinned Job approval/receivable identity; their originals cannot safely qualify for final release. Job-scoped V1 final delivery is the next implementation step.
- Added finance/production boundary rules tests, proof rendering verification, and isolated PostgreSQL finance tests (environment gated). Native build/testing in progress at this checkpoint; UI suite currently 94 passed. No merge or deployment.

## Resumed implementation — Public V1, webhooks and reference website

- Implemented signed/scoped V1 booking submission/status and idempotency on existing Pending Job intake.
- Added Job-scoped published-version gallery pages, watermarked proofs, customer selections/submission and server-gated final attachments. Existing client access issues a reusable V1 customer capability; no private storage URL is exposed.
- Added OpenAPI and frozen compatibility baseline; standardized V1 errors, bounded JSON, pagination and persisted public rate limits.
- Added durable webhook event/delivery tables, transaction-time booking/publication events, Finance-aware availability scan, HTTPS HMAC delivery with leases and bounded retries. Operations can see endpoint acceptance/retry history.
- Added reference website/BFF using only V1 and durable signed webhook receiver with duplicate suppression. Node tests verify proxy route restrictions, server-only secrets/signatures and duplicate receipt handling.
- Added closed Job/operational child guards for alternate writers and controlled Operations reopening. This migration requires PostgreSQL acceptance before merge.
- Current verification: native 243 passed / 15 environment-gated skipped; UI 95 passed; reference site 3 passed. All core database, webhook transport and actual-storage acceptance remain unverified here. Android/iOS acceptance remains blocked by the platform environment.
- Integration configuration, API and exact reference-site run commands are in `docs/PUBLIC-V1-INTEGRATION.md`.
