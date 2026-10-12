# Job template and visual workflow implementation checkpoint

Requested: October 12, 2026. Repository: fitzroywright/Aegis.Studio.
Upstream source inspected: main at 3381b5dde65411c205f8fcd6f1ae8eaccbb1c37f.
Working branch: feature/job-workflow-graph-20261012. Do not deploy to Kratos.

## Requirements and boundaries

Implement the full user-provided job template, visual graph editor, conditional POS branches,
Front Desk selection/preview, Job Details and Production execution, safe applied-template
changes, versioning, comprehensive demo scenarios, and meaningful local tests.
Studio demonstration records are disposable; unrelated systems, shared data, configuration,
secrets, and unrelated S3 objects are outside reset authorization. No feature flags.

## Source acquisition

No current checkout was present in this turn's directory. An older complete local checkout
was copied into this workspace and reconciled against the connected GitHub main tree.
The local baseline commit is a source snapshot; it is not the actual upstream commit.
GitHub updates must use the actual upstream parent/tree, never push this snapshot ancestry.
Common libraries are available through the sibling Common symlink.
.NET SDK: /workspace/scratch/0b27c7c33946/dotnet/dotnet.
PostgreSQL binaries: /workspace/scratch/0b27c7c33946/test-deps/pgserver.

## Implemented so far, not yet verified

- Pure DAG model, validation, nested subflow expansion, service expression evaluation,
  All/Any joins, inactive branch distinction, and existing-task status evaluation.
- Job graph task bindings in studio_job_activities; snapshot definitions in existing job
  template instance table; service selection through existing catalog agreements.
- Template change impact, preservation of removed completed work, history snapshots,
  revision checking, operation replay validation and workflow change audit.
- Schema extensions for drafts, task bindings, service selection, operations and history.
- Dagre 1.1.5 retrieved for vendored client layout (license still to be added).

## Remaining

1. Compile and correct the graph engine/store; integrate task and completion guards.
2. Complete draft/publish endpoints and version access; validate catalog/employee references.
3. Build shared Dagre renderer and visual editor; integrate Front Desk preview/service choice,
   job template change preview, operational task list and Production visibility.
4. Demo generation with new graph scenarios; retain genuine existing demo media.
5. Run pure graph, PostgreSQL integration, UI and existing regression tests and correct defects.
6. Commit checkpoints to the dedicated GitHub branch; deliver honest final report and commands.

## Validation status

No new functionality has been verified yet. Baseline --no-restore build failed because the
current environment does not have the referenced NuGet packages. Restore is being investigated.
Do not report tests as passing until actually executed.

## Second implementation checkpoint

- Draft save/publication/version endpoints written, with catalog and employee reference checks.
- Shared Dagre canvas, integrated Job Template designer, visual port connections, branch
  reconnection, nested flow editing, service preview and node properties written.
- Front Desk service selection/preview and atomic booking graph creation integrated.
- Job Details graph view, Apply or Change Template impact, assignment/progress actions,
  service changes, preserved prior work and Production workflow task view integrated.
- Demo graph examples added to comprehensive existing demo generation without media changes.
- Pure graph tests written. None reported as passing yet.
- Package restore resolved using a temporary local NuGet feed. Build discovered stale local
  Common.Messaging missing IEmailRecipientDirectory; acquiring current dependency source.
- Native local PostgreSQL cannot start under this restricted root runtime (user switching
  unavailable). Investigating PGlite for transaction tests; concurrency must still be tested
  against native PostgreSQL if the embedded runtime cannot verify it.

Next: compile/correct; add integration and visual tests; fix UI/layout defects; verify;
checkpoint results; prepare final delivery. No deployment and no merge into main yet.

## Verification checkpoint

- Pure graph model/engine/example files compiled with the .NET 10 Roslyn compiler.
- Four new graph UI tests executed and passed (shared joins, nested expansion/collapse,
  status/zoom/fit/selection/connections, and a 100-node graph).
- Existing UI suite: 103 passed, 52 failed. Investigating upstream baseline failures
  separately from introduced defects; do not label all failures preexisting yet.
- Job Details fixture now loads the graph dependency and explicitly returns no graph
  for jobs without templates; the workflow-dialog interaction check now passes.
- Draft/booking/template-change/service-removal/concurrency PostgreSQL tests written.
- Temporary PGlite server available; persistence tests not executed yet.
- Current local dependency sources: Common.Messaging main 183dba11388500726abed74129295504dd10fea8,
  isolated at sibling CommonBuild; all other existing Common projects reused locally.
- Full .NET build still being investigated; it has not passed. Compiler worker limiting
  enabled locally to avoid runtime resource problems. No build settings changed in repository.
- Chromium verification script prepared; browser runtime installation in progress.
- Latest GitHub checkpoint before this checkpoint: 1092ff1727539a417170619b2091cd35635408c5.

## Verified implementation and final integration review

- Full Web/Web.Tests builds passed repeatedly with zero warnings/errors.
- Twelve original graph engine tests passed. Four additional dependency and complementary
  conditional-path cases have since been added and need a fresh final test run.
- Eight graph/editor UI tests passed; Chromium checks confirmed shared-node rendering,
  real branch creation, no page overflow at 1440x900 and 1366x768, and readable 12px labels.
- Final updated existing UI suite: 112 pass, 51 fail. Pristine upstream: 104 pass, 51 fail.
  The final changed suite has no additional failure names; the 51 baseline failures remain.
- Five persistence tests passed with explicit schema initialization and No Reset On Close
  in the temporary PGlite connection. The separate Front Desk creation/retry/task execution
  test passed after fixing structural comparison of service arrays.
- The financial test now includes a posted sale; rerun after the final build.
- Native PostgreSQL concurrency and multi-session stress remain unverified because this
  runtime cannot launch native PostgreSQL as a non-root user. PGlite is a single-session
  multiplexer, so its concurrent-update result must not be presented as native stress proof.
- Final integration corrections: production approval excludes future Delivery/Archive tasks;
  actual recorded delivery completes eligible graph Delivery milestones; post-delivery
  Archive work remains actionable; graph-inactive tasks are excluded from legacy gate SQL.
- Added a production-before-delivery/milestone replay persistence test.
- Current final integration build is running. Full non-database .NET suite previously
  returned 303 pass, 2 newly added dependency checks failed against an older Web assembly,
  37 skipped. Those checks were corrected in source; rerun against the final rebuilt assembly.
- Nothing deployed to Kratos; no external data reset; no merge into main.

## Interruption and resume checkpoint — October 12, 2026

The local execution environment disconnected while the final integration build and
verification pass were running. Native execution returned:
"409 Conflict, environment_offline: Environment is not connected."
A read-only reconnection attempt also failed. No final build/test result could be retrieved.

The implementation is NOT declared complete or fully verified. The latest branch includes
the integrated graph engine/editor, draft publication, POS conditions, Front Desk, job
changes, Job Details/Production, demo graph scenarios, and lifecycle gate corrections.
A final static review corrected a multiple-declarator var statement in the new conditional
test and retained the post-delivery milestone refresh and canonical graph CSS rules through
GitHub after local execution became unavailable. These last corrections are unexecuted.

Last executed evidence:
- Repeated full Web/Web.Tests builds: zero warnings/errors before the last integration changes.
- Initial graph engine suite: 12 passed. Latest 16-case engine suite needs execution.
- Graph/editor UI suite: 8 passed before the last port/style cleanup.
- Updated full UI suite: 112 passed / 51 failed; upstream baseline had the same 51 failure names.
- Embedded PostgreSQL: five persistence checks passed together; Front Desk journey passed
  separately after the booking retry fix. Latest posted-sale safeguard and delivery milestone
  additions need a fresh run against the final build.
- Chromium editor checks: no errors, one shared reconnection node, no page overflow at
  1440x900 and 1366x768; dark renderer screenshot captured.
- Full .NET suite before fresh engine assembly: 303 passed / 2 new dependency checks failed /
  37 skipped. Do not report this suite as passing. Rerun after rebuilding.
- Native PostgreSQL multi-session concurrency and full demo regeneration remain unverified.

Resume from feature/job-workflow-graph-20261012. Do not merge or deploy before finishing
the requested acceptance checks. Nothing was deployed to Kratos or reset externally.

Suggested native local commands (from the Studio checkout; Common sibling repos required):
```bash
git fetch origin
git switch feature/job-workflow-graph-20261012
git pull --ff-only
dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build
# Set STUDIO_TEST_POSTGRES to a disposable native PostgreSQL database, then rerun tests.
cd tests/ui
npm ci
npm test
```

Update Common.Messaging to its current main if IEmailRecipientDirectory is missing.
Do not use the temporary local source-snapshot commit ancestry for a git push: GitHub
branch ancestry is authoritative. The local source checkout was materialized through the
connector; its baseline commit differs from upstream even though its baseline sources match.



## Resumed verification — October 12, 2026

This section supersedes the earlier interruption and unexecuted-results status.

- Recovered source from GitHub checkpoint 510aae63f41e7ba23ed7d1750056dc59f1bd2000 into a fresh workspace.
- Reconciled all current main changes through e19c5994efc6dc8eddd9097c09337d6db42de4f4,
  preserving the latest Operations navigation, Workspaces rail, profile menu, page descriptions,
  booking action, search styling and canonical CSS migration. Resolved the stylesheet conflict.
- Graph CSS is now loaded from css/components/workflow-graph.css, in the current CSS structure.
- Full Web/Web.Tests build: zero warnings, zero errors, after the final backend changes.
- Full non-database .NET suite: 307 passed, zero failed, 39 database-dependent tests skipped.
  The expanded graph engine suite accounts for 16 passing cases.
- All eight graph/editor UI tests passed.
- All eight workflow persistence tests passed against fresh embedded PostgreSQL (PGlite).
  Coverage includes draft/version isolation, validation, Front Desk creation/retry/task execution,
  template impact/replay/completed-work preservation, posted-sale safety, update revision conflicts,
  production/delivery milestone guards and replay, and demo graph scenarios/retry idempotence.
- The new demo test found and fixed a real defect: the graph seed marker omitted the required
  studio_state.updated_at_utc timestamp, which would roll back the rich-demo transaction.
  The test now verifies eight templates, ten jobs, conditional/started/blocked/completed scenarios,
  preserved previous-template work and a retry without duplicate templates or activities.
- Chromium 153: editor interaction created a conditional branch connected at both ends to
  one shared destination; no page errors; no page overflow at 1440x900 or 1366x768; 12px labels.
  These checks used production HTML/CSS/scripts and deterministic API fixtures, not a live server.
- Latest integrated UI suite: 109 passed, 58 failed. Pristine main at the commit above:
  101 passed, the SAME 58 failure names. No additional failure names from this branch.
  This is a scoped regression comparison, not a claim that the overall UI suite is green.

### Remaining acceptance boundary

Native PostgreSQL cannot start in this runtime because switching to an unprivileged user is
blocked. The PGlite socket server multiplexes a single PostgreSQL session. Its passing revision
conflict test does not prove native multi-session locking or stress behavior. Run the supplied
persistence tests on disposable native PostgreSQL before merging. Other database-dependent
regression classes remain unexecuted in this resume; the full-suite skip count is explicit above.
Full media-backed demo regeneration was not run: the new test verifies the graph seed itself,
not public image downloads, S3 storage, or the entire historical seed. No external demo reset,
Kratos deployment or merge into main occurred. PR #118 remains a draft pending native/database
and whole-demo acceptance. See docs/JOB-WORKFLOW-20261012-VERIFICATION.md.

The fresh local git repository is a recovery snapshot, not upstream ancestry. The GitHub merge
checkpoint has the actual feature and main commits as parents. Never push local snapshot ancestry.
