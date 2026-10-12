# Full demo regeneration and consolidated Studio Management

Requested October 12, 2026. Base main a922ef4b64a4f22849e6f680c5ee2c6e82561bc7.
Working branch feature/full-demo-studio-management-20261012.
Checkpoint 66436d6b3f0e6eecf3d82ee7b6a36ad7830541b3 retained intermediate recovery source.

## Final behavior

Reset and Regenerate queues a server-side run that purges Studio-owned files, clears Studio
business records, and invokes the full operational/photo/template generator. The browser polls
persisted status and reports completion only after the full generator commits. Generate Demo
Data uses the same generator without resetting existing records; interrupted loads can resume.
Staff accounts, configuration, access roles and the permanent Unassigned workspace are retained.
A running progress record survives reset; completed run records and graph seed markers are
cleared. Database reset honors the configured schema rather than hardcoding public.

The historical demo seeder now enables the existing transaction-local studio.demo_seed setting
in all dense and rich seed transactions. This prevents closed-job guards from rejecting seeded
completed scenarios and suppresses workflow notification side effects during seeding. The
setting expires at transaction end; normal operational protections remain unchanged.

The full dataset includes six operational workspaces, 126 dense jobs over 21 days, historical
photo jobs with at least 40 assets each, photographers and availability, schedules, clients,
bookings, messages, subjects, collections, invoices, payments, refunds, production/QC examples,
eight published Job Templates, ten graph job scenarios and two Work Order Templates linked
to demo retouching/printing catalog services. Existing demo media source catalogue is retained.

Studio Management now contains catalog, Job Templates, Work Order Templates, staff/security,
workspaces, automation, service health, announcements and all demo controls. Old admin bookmarks
route to Studio Management. Administrator access remains enforced. The avatar menu is now
Preferences, Studio Management and Sign Out; the duplicate Profile and Administration entries
are removed. Preferences retains identity, theme and messaging controls. Role/announcement
widgets remain inside the central card's scrolling area. The outer page does not scroll.

## Executed validation

- Clean Web/Web.Tests build: zero warnings/errors.
- 18 focused loader/management/menu/graph/editor UI tests passed.
- Two embedded PostgreSQL persistence checks passed: reset preservation/progress bookkeeping,
  and full purge/rebuild including photo assets, templates, graph instances and catalog links.
- The full-rebuild test uses the genuine repository portrait fixture in the photo source cache
  and local storage. It does not test public photo downloads or Kratos S3 availability.
- Chromium checks passed at 1440x900 and 1366x768 with no document overflow; consolidated
  Preferences opens and the avatar menu closes after selection.
- Full non-database Web suite: 307 passed; 42 database-dependent tests skipped.
- Full UI suite: 116 passed and 58 existing failures. Earlier upstream comparison had the same failures.

The asynchronous coordinator acceptance test is tagged Database=Native: progress writes require
independent database sessions and cannot be validated on the PGlite single-session multiplexer.
Native PostgreSQL coordination, real public photo downloads and Kratos S3 acceptance remain
local checks. No external reset or deployment has been performed.

After pulling main and deploying, use Avatar > Studio Management > Demo Data > Reset and
Regenerate. The existing dedicated-storage and demo-reset configuration checks still apply.
For native acceptance, set STUDIO_TEST_POSTGRES to a disposable database and run:

```bash
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build --filter FullyQualifiedName~FullDemoRegenerationTests
```
