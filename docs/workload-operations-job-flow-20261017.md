# Workload, Operations and compact job flow

## Baseline and reuse

Based on main `3f5a5e8052cc3fd389e745ac31e6ad852401cdec` (October 17).
Inspected current Production, Operations, Schedule, Job Details, JobGraphStore, WorkflowGraphEngine,
CatalogWorkOrders, ProductionOrderLinker, ProductionItems, workflow commands and JobAccessService.
Reviewed open PRs, including #127 (actual workflow route) and #145 (Front Desk Overview selection).
Their relevant current-main code was reconciled; neither branch was merged or overwritten.
No AGENTS.md was present in the repository tree.

The canonical Production route remains the existing catalog work-order queue. The separate operational
activity queue and workflow-task screen remain available. Schedule keeps photographer calendars and
job/production timing views. Template design retains its full canvas and publication/edit commands.
Clients, gallery publication, statements, Front Desk, navigation order, financial writes, workflow
transitions, approvals and audits were not replaced.

## Quantity semantics

`GET /api/production/workload` is a permission-filtered, repeatable-read projection of existing records.
It reads activities, item quantity/QC aggregates and one record per catalog work order, and evaluates
job graphs with the existing engine. Queries are batched; it does not fetch media collections or call
each job's endpoint. The existing work-order GET accepts up to 50 selected order IDs while retaining
its existing accessible-job restriction and all existing query behavior when IDs are omitted.

* Activity counts are labeled activities, never prints. Jobs are distinct job IDs.
* Purchased PRINT/ID item records are recorded physical outputs. Generic approved-media records are
  image/item records; other purchased outputs remain separate. These are not added into one total.
* Produced quantities, outstanding quantities, pending QC and rejected-item rework use item records.
  Cancelled items contribute no remaining work. Existing refund-adjusted quantities are used without
  recalculating or writing refund history. Produced/refunded exceptions remain visible.
* Posted legacy output lines awaiting source selection use net sale quantities after refunds, only
  when no item or catalog-production order owns that deliverable. Their existing source-selection
  screen remains the corrective destination.
* A catalog order's quantity is counted once even if several stages match a filter. It is labeled
  **ordered units**, not actual produced units or inferred physical prints. Catalog task completion
  does not supply partial output quantities: the UI explicitly reports this as unknown.
* Linked quantity records unavailable under ManageProduction remain unavailable, not asserted to
  be zero. Missing linked quantity records are displayed explicitly. Generic approved images can
  describe the same source as an output order and are deliberately displayed separately.
* Existing graph prerequisites, order exceptions, source requirements and host approval stages
  distinguish waiting work from ready work. No UI filter changes workflow state.

## Operations and navigation

Operations provides job-level drilldowns for Delivery activity due dates, delivery concerns, unknown
delivery dates, existing stage-time alerts, overdue activities, waiting work, unassigned activities
and recorded handoffs awaiting completion. A future delivery date with blocked or unassigned work is
an explainable concern, not a predicted finish time. Stage warning/overdue thresholds are labeled
separately from delivery dates. Assigned staff workload is an activity count, not available capacity.

Workspace, stage, staff and attention selections survive revisiting Operations. Production links
carry the selected job IDs and relevant activity/staff/workspace filters into the existing queue.
The plan link retains production area, staff and workspace context. Deposit overrides continue
through the existing Operations Manager decision screen and existing self-approval restrictions.

## Compact Job Details

Jobs with template graphs use their evaluated graph, task assignments, optional/skipped states and
blocker reasons. Inactive branches are omitted. Actual dependency ranks group parallel dots without
inventing links between independent tasks. Subflows expand between Stage Start / Stage Complete
endpoints with a dashed line; component dots appear below it. Only one main stage expands at once,
with nested substages kept within that stage. Other main stages stay visible.

Completion requires evaluated subtree completion and all applicable tasks to be complete/skipped;
optional work is not silently treated as done. Component details show status, exact recorded blocker,
responsible person/role or Unassigned, and dependencies. They open the existing task command screen,
focused on that task. No new write command, approval bypass or completion calculation was added.
Keyboard buttons, visible focus, collapse focus restoration and reduced-motion rules are included.
Jobs without a template retain their existing lifecycle/milestone presentation.

## Resource capacity boundary

No printer utilization, invented resource assignment, throughput or completion forecast is shown.
Reliable capacity needs explicit activity/output-to-resource assignments, equipment capability and
product/media-specific processing rates, operating availability, maintenance/downtime, setup/batch
assumptions and actual production/QC/rework quantities. Staff availability likewise needs workload
estimates joined to recorded working availability. Catalog partial production and per-output physical
unit classifications need authoritative tracking before expanding those totals. These are data gaps,
not a proposed second resource registry, queue or workflow engine.

## Verification

* 52 targeted UI regressions passed: workload/compact flow, graph/template editing, client behavior,
  photographer schedules and production planning. Ten tests directly cover this change, including
  partial quantities, refunds, QC rework, source/gate waiting, cancellation, missing quantity data,
  simple/substage/parallel/optional/completed workflows, navigation and Operations permission flags.
* Full UI comparison: untouched main 236 tests, 179 passed / 57 failed; branch 246 tests, 189 passed /
  57 failed. The failing test names match. Existing failing fixtures/assertions are not reported as
  successful regressions. Final JavaScript syntax checks pass.
* Client-only Node/jsdom sample: one subflow with 40 internal tasks, 5 warmups then median of 20
  renders: existing canvas 2.31 ms; compact projection 0.84 ms. This is not browser paint time,
  production latency or a database benchmark. Live request/SQL performance still needs measurement.
* .NET SDK and PostgreSQL are absent here. C# compilation, new SQL execution, HTTP permission checks
  and the required Debug/Release build/test matrix have **not run**. Existing database integration
  regressions for production/refunds/QC remain required.
* Real rendered theme/viewport QA remains **unverified**: local browser installation failed and the
  separate browser could not reach the workspace server. Standard, Light, Dark, narrow layouts,
  scrolling and reduced-motion styles were inspected in source/DOM, not certified from screenshots.

Required local checks before marking ready:

```sh
dotnet restore Aegis.Studio.slnx
dotnet build Aegis.Studio.slnx --no-restore --configuration Debug
dotnet test Aegis.Studio.slnx --no-build --configuration Debug
dotnet build Aegis.Studio.slnx --no-restore --configuration Release
dotnet test Aegis.Studio.slnx --no-build --configuration Release
cd tests/ui
npm ci
npm test
```

Run PostgreSQL coverage with the repository's existing disposable `STUDIO_TEST_POSTGRES` setup;
verify the new workload GET with administrator, production manager, assignee, viewer and inaccessible
jobs. Check posted output lines awaiting source selection and partial/full refunds against the actual
order/QC screens. Inspect representative jobs in all three themes at desktop, tablet and narrow widths.
Measure workload response time, query count and page latency with representative production volume.

## Deployment and rollback

This is a draft review branch, not merged or deployed. No schema or business-data migration is added.
Deploy the backend and versioned static assets together using the established Studio deployment
process only after validation and approval to merge/deploy. Roll back by redeploying the previous
application artifact; this projection introduces no stored state to undo. Existing financial/workflow
history and commands remain authoritative throughout.
