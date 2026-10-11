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
