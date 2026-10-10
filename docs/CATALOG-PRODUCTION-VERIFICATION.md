# Catalog, templates, Front Desk POS and production

## Audit and component reuse

| Existing component | Extension |
| --- | --- |
| `PosCatalogStore` / `studio_pos_catalog` | Authoritative prices; description, estimated cost, tax, options, package components, production requirement and Work Order Template links. `price_list_key` reserves future price-list expansion. |
| `StudioPosStore` / `Aegis.Pos.Core` | Existing sale, tender, receipt, register, refund, accounting and replay handling; frozen catalog/template/price snapshots and confirmed Job agreements. |
| Existing Job Template definitions and `studio_job_activities` | Managed immutable Job Templates and Work Order Templates; instantiate the existing activities, with order/dependency/area/skill/estimate/material/QC/default-assignee metadata. Existing templates and task APIs remain. |
| `WorkflowStore` / `OperationalWorkStore` | Existing staff assignment, revisions, activity progress and review/delivery gates; enforce prerequisites, approved-image requirements and final QC. In-house templates can omit photography scheduling. |
| `ProductionOrderLinker` | Per-sale-line, per-catalog-product work orders using frozen template versions; legacy product handling retained for older catalog items. |
| WorkDay and employee directory | Existing scheduling/availability and assignment mechanisms retained; default assignees must be active and assignable. |
| Media Upload, Job Gallery, Messages and Customer Gallery | Existing intake/import/storage and read-only publication mechanisms retained. Work orders pin approved asset versions. Explicit publication is available through Job Gallery and is blocked by incomplete production/QC. No gallery implementation was deleted. |

## Implemented behavior

Studio Management exposes Product & Services Catalog, Job Templates, and Work Order Templates. Templates support create/edit/duplicate/activate/retire and immutable version history. Catalog items link to reusable Work Order Templates; packages retain component quantities. Product variations match specification text; non-applicable steps remain recorded as skipped. Final QC is mandatory.

Front Desk provides New Sale, Take Payment, Record Deposit, Issue Refund, View Receipt and Close Register. Actor-bound, expiring launch tickets open a separate full-size browser window directly in the selected operation. Backend writes require the matching action and context. Dirty windows are preserved; sale drafts persist in session storage; the parent refresh retains its state and scroll. General financial-event writes direct staff to Front Desk/POS.

Production defaults to pending work, with area filters, bounded server pages, assignments, priority, deadlines, dependencies, QC and exception visibility. Approved-source selection is searchable and paginated. Specification amendments and refunds retain production history and require reconciliation when necessary. Existing version snapshots and completed work are retained.

Two existing fresh-start issues found by the real-app check were fixed: workspace appearance columns were read before the media migration, and HTTP request audit had collided with Job operational audit. HTTP logs now use their own existing logger/store implementation and a distinct table; legacy HTTP rows are copied where that prior schema exists, and the original audit table is retained. The existing activity reader also now selects the revision column it already expected.

## Validation, 2026-10-10

- Restore succeeded using `dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj -m:1 -p:RestoreUseStaticGraphEvaluation=true --ignore-failed-sources`.
- Build: zero warnings and zero errors.
- .NET suite without a database: 189 passed, 13 database tests skipped, 202 total.
- Commercial tests against an isolated PGlite PostgreSQL-protocol socket: **6 passed, zero failures/skips**. Covers authorized pricing, dependency validation, final QC, action/context matching, version/price freezing, agreement fulfillment, sale/completion/refund replay, real approved photographs, actual existing activity assignment/progress, package quantities, variations and retained cancellation history.
- Focused POS launch UI tests: **5 passed**. Covers exact action/context, clean-window reuse, dirty-window preservation, non-Front-Desk rejection, launch failure cleanup and completion-message source validation.
- Full UI suite: **33 passed, 13 failed, 46 total**. The same 13 failures were reproduced on untouched main before these changes. They concern the existing shared shell, Today startup/navigation, workspace/Jobs rails, original Production collection fixture and Front Desk search fixture. No new failing test was introduced.
- Real application HTTP check: fresh migration/startup and mock login, management APIs, launch issuance/resolution, missing-launch/case-variant write rejection and generic-launch rejection passed. A catalog product and both templates were created, a Front Desk booking instantiated its Job, and a contextual sale/completion retry produced exactly one work order for three products. Approved-source API also returned a bounded result.
- Existing database integration class on PGlite: 7 passed; `WorkDayReschedulePreservesIdsAndRollsBackConflicts` timed out while its transaction accessed another connection. This is unresolved on native PostgreSQL; PGlite protocol checks do not prove native concurrent-session behavior.
- Chromium could not be downloaded in this environment. Visual browser QA and a complete native PostgreSQL/WorkDay regression run remain validation limits. External Customer Gallery publishing was not exercised.

## Connected acceptance scenario

1. Define the framed photograph Work Order Template: preparation → printing → framing → final QC; create the priced catalog product.
2. Include three products and their specifications in a Job Template; create a real Front Desk booking/Job from that template.
3. Confirm the catalog agreement, change the catalog price, and sell through the existing POS engine: the original agreed price remains. The real HTTP scenario separately verifies Front Desk launch and sale endpoints.
4. Repeat creation/completion: one work order remains, quantity three, with the original frozen Work Order Template version even after template edits.
5. Link a real approved Job Gallery photograph. Starting dependent work prematurely is rejected. Assign and complete each activity through `OperationalWorkStore`, including final QC.
6. Required production/QC gates now allow preparing collection or the explicit authorized Job Gallery publication step; the test does not publish externally.
7. Refund one product twice: one refund remains, quantity becomes two, completed task history remains, and publication is blocked until a manager reconciles the exception.

## Reproduction

Use the normal repository .NET/Common project layout. Build first. Set `STUDIO_TEST_POSTGRES` to a disposable PostgreSQL database to enable database tests; run `dotnet test ... --filter FullyQualifiedName~CommercialProductionTests`. Run `npm ci --prefix tests/ui` and `npm test --prefix tests/ui` for the UI suite.

`tools/verify-commercial-production.py` runs the real Debug application in Development mode against the disposable connection supplied in `STUDIO_SMOKE_POSTGRES`. Set `DOTNET_EXE` if dotnet is not on PATH. It creates test catalog/template/booking/sale data and uses mock authentication. It is a local acceptance harness, not an external deployment test.
