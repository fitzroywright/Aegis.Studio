# Complete workflow brief — implementation and acceptance checkpoint

This branch remains incomplete and is not a release candidate. Compilation, rule tests and browser-controller tests do not establish PostgreSQL transaction safety, live messaging delivery, storage acceptance or mobile-device readiness.

Branch: `feature/studio-complete-workflow-20261011`. Baseline: `d12121c99e53e69c227202a0faa2a42d62bd746a`.

Remote recovery checkpoints: audit `be10b95fa8ab2ef9b5f0d1b532c78f62f206c7cb`; initial workflow `4e6119598bf12c1bc44fdf62d606ec3521ae3c84` and `ba6ee51775c5f6652e042800d3d8426402e13b5e`; Finance/production/proofs `831aa0ba257f9fb14cbdb49ad776e963a25915bb`; Public V1/webhooks/reference website `067c9ee76bff86bc6aabe560f8c0ee36b28cbb84`. Fetch the latest branch head to include subsequent ownership/reassignment work.

## Earlier verification (before the resumed changes)

.NET Web/Core compilation and automated tests run against SDK 10.0.401 and the existing Common projects. The earlier suite passed **248**, skipped **15**, failed **0**. Browser-controller tests passed **99**, including four ownership-response tests. Reference website tests passed **3** at the preceding checkpoint. The resumed JavaScript files pass static parsing and `git diff --check`; builds and tests were not rerun following the user instruction to perform them locally.

- Pending Job intake, deposit threshold rules and independent Operations decision authority.
- WorkDay reporter-only API boundaries, explicit acknowledgements, version chaining and response rules.
- Finance authorization rules and production dispatch/reporting authority, including combined-role cases.
- Reduced-resolution, visibly watermarked image rendering.
- Public V1 request authentication, required fields, stable response shape and frozen OpenAPI compatibility baseline.
- Reference HTTP proxy signing, restriction to V1 routes, server-only credentials and durable webhook duplicate suppression.
- Operations decision navigation, Finance receivables navigation and existing application controller behavior.

These tests include pure rules, middleware, image pixels, DOM behavior and a local Node HTTP test with a stubbed upstream. They do not constitute an end-to-end deployed workflow demonstration.

## Implemented, awaiting connected acceptance

| Area | Current implementation | Evidence still required |
| --- | --- | --- |
| Booking and deposit | Intake immediately creates a Pending Job. Manual receipts are auditable and do not automatically confirm. Deposit requests enqueue email through the existing provider worker, reuse the message ID and show queue/provider evidence. | PostgreSQL concurrency and outbox acceptance; configured test email; confirmation through real role accounts. Recipient delivery is not claimed without receipts. |
| Operations | Separate Operations page, override request/approve/reject, self-approval rejection, history/direct links and authorized reopening. | Real-account navigation, concurrent decisions and overdue/escalation behavior. |
| Ownership | Current assignment and acceptance/decline history; Manager reassignment supersedes previous assignment; decline returns unresolved ownership and notifies Manager, escalating a Manager's own decline to Operations. | PostgreSQL migration/replay; same-person Manager/Owner and overdue cases; all legacy owner writers. |
| Scheduling and WorkDay | Per-photographer reports, current-assignment checks, safe operation replay, shared-session completion and Studio history. Removal/reassignment bumps the assignment version, including same-person re-addition, while preserving old reports. | Multiple sessions/photographers, resource conflicts and concurrent rescheduling/replay on PostgreSQL. |
| Mobile | Photographer assignment UI, acceptance/decline/reason, arrival/start/finish, notes/problems, availability interface and offline outbox; account-bound cache and revoked-token boundaries. Excluded upload/timekeeping controls are removed from the active flow. | Android/iOS builds and actual devices; network loss/restart/revocation; explicit recovery from offline conflicts. |
| Finance and delivery | Explicit Finance Manager role; audited client/Job authorization, amount/scope/terms/due date/reference/approver/revocation. Ledger and invoice balances are checked at final release. Delivery uses connected workflow. Receivables survive operational closure. | Transactional payment/refund/credit tests; reconciliation of shared receipts across multiple invoices; all alternate final-delivery paths. Non-JMD invoices intentionally cannot be released through the current JMD gate. |
| Production and approval | Explicit Production Manager dispatch and final technical approval; assigned staff can report progress. Review/publication identifies media versions. | Connected and legacy writer audit, task/revision/resubmission flow and exact stored-version acceptance. |
| Galleries | Legacy public gallery access serves proofs only. V1 publishes approved Job versions with expiring/revocable client access; selections/submission and final downloads use Job-scoped authorization. | Real stored media, publication changes, expired/revoked tokens, Finance changes during access and server-side denial with unpaid balances. |
| Public V1 | Signed/scoped HTTPS booking/status, idempotency, published-gallery pagination, proofs, selections and gated final downloads; schemas, standardized errors, bounded JSON, rate limiting and version policy. | Actual HTTP requests with PostgreSQL and storage; full OpenAPI conformance and concurrency acceptance. |
| Webhooks | Durable event/destination outbox, signed HTTPS payloads, leases/retries, stable event IDs, Finance-aware delivery availability and Operations delivery visibility. | Real worker transport against a test receiver; crash/retry/configuration-change cases; migrations/triggers on PostgreSQL. |
| Reference website | Runnable booking/gallery BFF and signed durable webhook receiver; browser uses V1 exclusively. | Browser booking-through-gallery demonstration against a configured test Studio. See `PUBLIC-V1-INTEGRATION.md`. |
| Closure | Database guards reject closed Job and operational child changes; reopening sets an Operations-authorized transaction flag. Financial activity and preserved histories remain available. | Full migration and direct legacy API bypass tests; closure and reopening with representative data. |

## Resumed implementation, awaiting local validation

The subsequent source changes cover WorkDay conflict/history recovery and offline availability, Finance shared receipt counting, named-role production controls, configured WhatsApp deposit requests, actual storage-backed upload receipts and resumable batches, manual per-photographer Studio reporting, resource/availability conflict enforcement, legacy automation boundaries and fenced preview retries. Read the progress log for the implementation details.

New database test cases have been added but **have not been executed**. The earlier pass/skip counts above are not evidence for the current branch.

Remaining acceptance gates:

1. Build the current Web/Core and platform projects and run the automated suites locally with a disposable PostgreSQL service. Resolve any compilation, migration, transaction or test failures before proceeding.
2. Demonstrate the complete connected workflow with real stored media, test messaging providers, multiple roles/photographers, network interruption and the reference website. Verify both the connected controls and direct legacy API denials.
3. Build and exercise WorkDay on Android and iOS devices, including offline queueing, app/device restart, account/session changes and conflict recovery. Record results before release review.

See `COMPLETE-WORKFLOW-20261011-LOCAL-HANDOFF.md` for commands, configuration and concrete scenarios. Source implementation does not establish these acceptance gates as passed.

## Environment blockers and next actions

| Blocker | Evidence | Next action |
| --- | --- | --- |
| Disposable PostgreSQL | `STUDIO_TEST_POSTGRES` is absent. The bundled PostgreSQL binaries require a non-root account; this runtime rejects user switching with OSError 22. Fifteen database tests skip. | Provide a disposable PostgreSQL service, run the gated tests, add connected workflow transaction coverage and verify migrations. Never use production client/payment data for tests. |
| Android | Missing `maui-android` workload produces NETSDK1147. | Use Android SDK/JDK and MAUI on a supported development machine; run `scripts/Build-WorkDay.ps1`, then device acceptance. |
| iOS | No Mac/Xcode/iOS workload/device in this Linux runtime. | Build and provision on Mac, then verify offline/restart and revoked-session behavior on device. |
| Messaging/storage/test deployment | No configured test recipient/provider or connected storage-backed Studio deployment was exercised. | Use Kratos test infrastructure and representative demo data, inspect provider and storage evidence, and run the complete role workflow. |

No merge to main, deployment, destructive database reset, production client/payment mutation or live message sending was performed. Do not merge or deploy until the incomplete requirements and connected/device acceptance gates pass.

## Resume

Fetch the implementation branch and read this report, `COMPLETE-WORKFLOW-20261011-PROGRESS.md`, the authoritative brief and `PUBLIC-V1-INTEGRATION.md`. The local snapshot was materialized through the GitHub connection; remote branch history is authoritative.

```sh
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --nologo -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false
npm ci --prefix tests/ui
npm test --prefix tests/ui
node --test examples/public-v1-site/server.test.mjs
```

Configure `STUDIO_TEST_POSTGRES` with a disposable service before running transactional acceptance. Mobile/platform checks are separate requirements.


## Additional WorkDay sync implementation — awaiting local validation

Automatic reconnect/active retry/resume sync, assignment Sync Now, live progress/failure feedback and durable last-success metadata are implemented. Encrypted cached Job/collection/roster targets and field-status reports now use stable operation receipts, reviewed target versions/fingerprints and the existing Owner/Manager notification/history path. New test sources have not been executed. See WORKDAY-SYNC-LOCAL-ACCEPTANCE.md for device/network/replay/conflict scenarios. Earlier verification counts do not validate these changes.

### Photographer field journey extension

The latest source adds the photographer mobile roster/status cache, automatic/manual durable synchronization, cross-assignment target conflicts, Job Details roster counts, scoped remainder scheduling, interactive workflow actions in Job Details and Front Desk, local Studio person searches and declared drag/drop uploads. See `WORKDAY-SYNC-LOCAL-ACCEPTANCE.md` for contracts and J1–J9 cases. New PostgreSQL/UI tests are **unrun**. Device/offline restart/reconnect, configured storage and role-based browser acceptance remain required; these objectives are implemented in source but not field-verified.
