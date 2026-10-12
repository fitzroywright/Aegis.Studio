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
