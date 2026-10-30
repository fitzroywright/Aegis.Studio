# Security modernization candidate — deployment gate closed

## Continuation checks

The follow-up removes the obsolete gallery wrapper around the deleted workspace row renderer. A full-script startup regression now exercises the actual index script order, the embedded Session Plan editor mapping, and navigation to Staff & Roles. The targeted provisioning, shell ownership and startup tests pass (14 tests, zero failures/skips) under Node 24 in the editing workspace. Five catalog/booking tests also pass after replacing a fixed history timer with the actual browser history event. This is not a Mac application build or live-provider acceptance.

The continuation UI suite is green: both `npm test` from `tests/ui` and `node --test --test-concurrency=4 tests/ui/*.test.cjs` recorded **473 passed, zero failed, zero skipped** under Node 24. Current fixtures exercise the approved overlay, shared drawer, footer workspace scope and canonical stylesheet owners. New regressions cover HTTP status propagation, clearing protected cached data after 401/403, and visible job/workflow error recovery. Production fixes also restore notification/message context, remove a retired planning binding, and retain closed status filter options. No browser screenshot or Mac application validation has been completed.

The UI candidate `d53803fe28bbd141cb198ef373c52e7350bb665b` passed all **476 UI tests with zero failures/skips** in coordinated CI. Follow-up [CI 41071718869](https://github.com/fitzroywright/Aegis.Studio/actions/runs/41071718869), source `1cc75487883179b19228c54bbd45e936a40857a4`, passed **437 Web tests / 6 failed / zero skipped**, **3 Domain tests**, and **476 UI tests / zero failures or skips**. Eight of the prior fourteen server failures are resolved, including the connected WorkDay journey and permission tests.

The next draft follow-up initializes external booking holds through their real schema owner, supplies the isolated rescheduling crew relations, and reuses the shared application fixture for the media/billing/booking journey. The media test now asserts that an ordinary caller cannot change production assignment before exercising the explicit manager path; quote conversion must retain a Pending Job. These latest edits await PostgreSQL verification. No failing assertions are skipped to waive a release gate. The editing workspace has no .NET SDK, so no local .NET or Mac validation is claimed.

This is a coordinated source candidate, not an approved production rollout. `security-baseline.json` pins every shared library; no application may substitute a floating Common.Security checkout or overwrite shared running binaries. Build in an isolated output directory and deploy a complete application bundle independently.

## Build and evidence

Run `python3 ../Common/Common.Security/scripts/verify-source-baseline.py security-baseline.json --common-root ../Common` before building. Run the existing application tests and the coordinated security workflow. Failed, skipped, missing or empty results block release. The workflow uses disposable PostgreSQL 16 and cannot replace live provider or operational acceptance.

## Data and identity preparation

1. Capture the currently deployed application package, protected configuration, database backup, key rings, plugin versions and credential-store references. Verify a restore into an isolated environment before any production write.
2. Review an owner-only approved account manifest. Preserve each existing account ID and login alias. Resolve immutable AD domain/user GUIDs or Entra tenant/object IDs through an authorized directory administrator. Email and usernames do not authorize bindings. No production identity import has been executed by this change.
3. Rehearse the additive common schema and approved imports on a representative restored database. Verify account counts, existing business references, role assignments and security audit. New staff/operators must remain Pending Access until explicitly approved.
4. Keep at least two approved administrators. Test login, disabling, linking/unlinking, role removal and session revocation before enabling broader access. All pre-cutover human cookies require a fresh login to acquire a verified account/version binding.

## Rollout and recovery

Stage the complete candidate alongside the last known-good package. Keep each application's database and deployment independent. Run its acceptance matrix, verify protected configuration and backups, then cut over only that application. No deployment is authorized by a successful shared-library build alone.

On a stop condition, stop the affected rollout and return traffic to the previous package. Preserve new business records and the additive security tables/audit. Do not restore a stale pre-cutover database after new writes; reconcile identity changes forward or recover current business writes into a restored copy before an explicitly approved restore. Do not drop old identity history or business tables. Re-run smoke and security checks after recovery.

## Required operational acceptance

Use the existing **Staff & Roles** page for authentication source, explicit immutable binding, approval, role assignment, workspace membership, disabling and revocation. Existing staff aliases, operational role tables, workspace/job ownership and historical records are retained. The page has one renderer and login has one script owner. A profile-write failure retains the safe pending account and form details for retry without creating another security account.

Configure optional Entra through `Authentication:Entra`, the existing Common.Secrets provider, and the registered callback. Local credentials and recovery use the shared account store. Development/demo mock identities require `IsolationConfirmed`, a server allowlist and an isolated database/storage/integration configuration; do not confirm isolation for a configuration pointing at production resources. Explicitly enroll/approve any demo operator and app roles; no default User/Admin role is created at login.

Run Studio's PostgreSQL persistence tests, UI ownership and behavioral tests, real AD/Entra login and the Staff & Roles approval/revocation matrix. The local UI suite passes; the PostgreSQL suite, live-provider checks and operational acceptance remain release gates.

Global role and directory-group changes, workspace membership and job membership writes use the shared security transaction for their existing Studio tables, account session-version change and security audit. Staff aliases resolve within the approved account directory; unenrolled metadata records cannot acquire operational roles through an alias match. Rehearse the approved account import before enabling these administration endpoints.

