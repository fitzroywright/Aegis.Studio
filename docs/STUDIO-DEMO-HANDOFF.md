# Studio demo implementation

The `studio-full-demo-ui` implementation follows the supplied Studio briefs and the attached My Work, Job Details, Front Desk, Finance and Management references. Workspaces, Pipeline and Production use the same shell and the operational behavior specified by the brief.

## Rendering ownership

`studio.css` replaces the previous workspace reference, Outlook workspace and Management stylesheets. It contains the shell and the eight screen layouts. Existing attachment, POS, profile and training styles remain feature scoped.

| Screen | Authoritative renderer |
| --- | --- |
| My Work | `renderMyWork` in `studio-navigation.js` |
| Workspaces | `renderWorkspaces` in `studio-navigation.js` |
| Job Details | Job branch of `thread`, reached through `openWorkspaceJob` in `workspace-outlook.js` |
| Pipeline | `showPipeline` in `scheduling.js` |
| Production | `showProduction` in `production.js` |
| Front Desk | `showFrontDesk` / `renderFrontDesk` in `frontdesk.js` |
| Finance | `showAccounts` in `management.js` |
| Management Dashboard | `showManagement` in `management.js` |

New workspaces receive a stable route key automatically on the server. Route key overrides are available only in collapsed Advanced / Integration Settings on creation and editing; normal edits preserve existing integration keys.

The navigation controller owns routing and startup. Global roles and announcements are Admin components, rather than wrappers that replace the Workspaces renderer.

## Demo graph

The full seed adds 27 jobs, six workspaces, 12 clients, four photographer profiles, linked bookings, schedule resources, sessions, production activities, invoices, payments, notifications, conversations, subject rosters, collections, document attachments and over 1,000 media records. Existing earlier demo jobs and user records remain available.

The wedding has a rehearsal and primary session, two payments and versioned media. Employee IDs, school portraits, graduation and corporate headshots have large subject rosters. Production activities identify their actual media collection. The ID roster retains subjects without photographs and retake states. Booking requests below the deposit threshold remain separate from jobs.

Real Wikimedia photographs pass through Common.Storage, with source URLs in metadata. Each version owns a storage path; downloaded bytes are cached, and demo JPEG derivatives are limited to 1200 pixels. Internet access is needed for the first seed. No image generator is used.

The v6 marker prevents subsequent demo loads from resetting edits. Loading an older dataset upgrades the full seed once. Asset and subject identifiers reserve 10,000 positions per job, preventing the previous large-job collisions.

Finance, Management and Job Details use signed financial events for charges, net receipts and remaining balances. Reporting filters and exports operate on the same dated transactions. Photographer workload follows schedule resource IDs. My Work counts all accessible job media and opens linked notifications and tasks.

Folder drops traverse nested directory batches. Subject drops target the selected roster subject. Existing ingest, returned-file processing, version history, storage, sending identities, roster editing, authentication and server permission checks remain in use.

## Validation

- Release solution build: zero warnings and errors.
- Solution tests: 151 Web tests and three Domain tests pass; three PostgreSQL integration tests skip without `STUDIO_TEST_POSTGRES`.
- UI suite: 22 tests, including a complete shared-shell smoke test, signed refund reconciliation, workload links, persisted production completion and repeated folder-directory batches.
- JavaScript syntax and whitespace checks pass.

This environment cannot launch Chromium because socket creation is prohibited. Browser screenshot comparison and a live database seed were not verified here. The DOM smoke test exercises all eight screen renderers with an interconnected API fixture; it does not establish pixel-level equivalence or database seed execution.

Run locally from the repository root:

```sh
dotnet build Aegis.Studio.slnx -c Release
dotnet test Aegis.Studio.slnx -c Release
npm --prefix tests/ui ci
npm --prefix tests/ui test
```

Set `STUDIO_TEST_POSTGRES` to a disposable PostgreSQL database to include the transactional persistence tests. In Development, use Admin's demo loader once, then walk from My Work through a job, its subjects/media/tasks, Pipeline, Production, Front Desk and Finance. Check persisted actions and photo downloads against the configured storage provider.
