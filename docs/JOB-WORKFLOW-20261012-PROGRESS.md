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
