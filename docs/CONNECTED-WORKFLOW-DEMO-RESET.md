# Connected Workflow demo reset

The Administration page offers **Generate Demo Data**, **Remove Demo Data**, and **Reset and Regenerate**. All three require an authenticated Studio Administrator.

On the demo deployment, explicitly set:

```ini
DemoData__Enabled=true
DemoData__AllowDestructiveReset=true
```

Restart Studio after changing environment variables. Remove `DemoData__AllowDestructiveReset` after using the reset controls.

**Reset and Regenerate** requires typing `REMOVE STUDIO DEMO DATA`. It runs in a single PostgreSQL transaction. It truncates `studio_jobs`, `studio_clients`, `studio_conversations` and every recursively discovered Studio foreign-key dependent table, refusing to touch protected authentication, workspace, or configuration tables. If a protected or non-Studio table depends on those records, PostgreSQL rolls back the entire operation. The endpoint does not delete external storage objects, and existing Workspaces remain. It is therefore **not** a full purge of every Studio database table or media object.

Generation creates 15 deterministic sample Jobs across all Connected Workflow stages, four demonstration Workspaces, fictional contacts with `example.invalid` addresses, Front Desk bookings and deposit receipts, conversations, WorkDay schedule/shoot sessions, crew assignments, and production tasks. Re-running generation is idempotent for the fixture IDs. No real email addresses are seeded.

## Validation

After pulling `main` on Windows:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

On the demo site, sign in as Administrator. Run **Reset and Regenerate** and verify Today, Jobs, Front Desk, WorkDay, Production, and Workspaces show the same Jobs. If the reset reports a protected dependency, do not remove the safeguard; inspect the live foreign-key graph and adjust the explicit ownership model.

## Limitations

The fixture is an operational navigation dataset, **not** a proof that upload backup verification, real asset delivery, and external gallery publication occurred. Later-stage Jobs intentionally do not fabricate media assets or backup proofs. External storage and legacy demo Workspaces are not purged by this implementation. PostgreSQL-dependent integration tests must run against an actual test database before production acceptance.
