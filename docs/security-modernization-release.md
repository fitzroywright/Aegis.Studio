# Security modernization candidate — deployment gate closed

## Candidate verification

The owner authorized coordinated source merges on 2026-10-30. Studio has been reconciled with current main, preserving media/session-plan, stage timing, demo-data and Staff & Roles updates. The combined local Node 24 UI run passes **503 tests, zero failures/skips**, and the CSS ownership guard passes. Coordinated [CI 41087696585](https://github.com/fitzroywright/Aegis.Studio/actions/runs/41087696585), tested source `2f815b756e96dee04efe730d91722edabf239cbd`, passes **443 Web, 3 Domain and 503 UI tests, zero failures/skips**. Shared-source verification, nonempty test evidence and CSS ownership checks pass. Source merge authorization does not authorize deployment or claim that the live acceptance gates below have passed.

Coordinated [CI 41074891503](https://github.com/fitzroywright/Aegis.Studio/actions/runs/41074891503), tested source `d08f496f82a80aaab887f8a344e167c2d2d0bb0c`, passes **443 Web tests, 3 Domain tests and 476 UI tests**, with **zero failures or skips**. The evidence gate confirms nonempty passing results, shared-source pins match the release manifest, and the CSS ownership workflow passes. All fourteen previously remaining server failures are resolved. Common.Security, Cafeteria, RequestPortal, Operations, Configuration and Diagnostics also retain passing candidate CI at their current draft PR heads.

The corrections initialize current isolated-fixture schema, reuse the shared application fixture for the media/billing/booking journey, and split parameterized setup statements. Assertions remain enabled. Coverage preserves nonzero WorkDay assignment generations, replay/conflict protection, denied production dispatch for ordinary callers, permitted manager dispatch, and a Pending Job after quote conversion. UI fixes clear protected caches on 401/403, restore error/retry paths and visible context, and use current canonical renderers and stylesheet owners.

The historical baseline workflow intentionally tests unchanged pre-modernization source and still reports its original failures. It is diagnostic evidence, not the current candidate or a runtime compatibility layer. No tests in the candidate are skipped to waive a release gate.

CI uses disposable PostgreSQL 16. Real provider callbacks, representative restored databases and operational acceptance have not been exercised. The editing workspace has no .NET SDK or browser executable; no local .NET build, Mac validation or browser screenshot acceptance is claimed.

This is a coordinated source candidate, not an approved production rollout. `security-baseline.json` pins every shared library; no application may substitute a floating Common.Security checkout or overwrite shared running binaries. Build in an isolated output directory and deploy a complete application bundle independently.

## Build and evidence

Run `python3 ../Common/Common.Security/scripts/verify-source-baseline.py security-baseline.json --common-root ../Common` before building. Run the existing application tests and the coordinated security workflow. Failed, skipped, missing or empty candidate results block release. The workflow uses disposable PostgreSQL 16 and cannot replace live provider or operational acceptance.

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

Run Studio's PostgreSQL persistence tests, UI ownership and behavioral tests, real AD/Entra login and the Staff & Roles approval/revocation matrix. The candidate UI and PostgreSQL suites pass; live-provider checks, migration/restore/recovery rehearsal and operational acceptance remain release gates.

Global role and directory-group changes, workspace membership and job membership writes use the shared security transaction for their existing Studio tables, account session-version change and security audit. Staff aliases resolve within the approved account directory; unenrolled metadata records cannot acquire operational roles through an alias match. Rehearse the approved account import before enabling these administration endpoints.

