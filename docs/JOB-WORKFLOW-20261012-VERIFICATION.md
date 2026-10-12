# Job workflow resumed verification

October 12, 2026. Repository: fitzroywright/Aegis.Studio. PR #118.

The final backend builds cleanly and all 16 graph engine, eight graph/editor UI, and eight
embedded PostgreSQL workflow persistence cases pass. The full non-database Web test suite
passes 307 cases, with 39 database-dependent cases skipped. The new demo-seed regression
caught and fixed the required timestamp omission in the graph seed marker.

Chromium checks use the actual current HTML/CSS and graph/editor scripts with deterministic
API responses. They verify connected conditional branch creation, one shared destination,
12px labels, and no document overflow at 1440x900 or 1366x768. They are component browser
acceptance, not a live authenticated end-to-end journey.

## Required native local acceptance

Use an empty disposable database; these persistence tests create and drop isolated schemas.

```bash
git fetch origin
git switch feature/job-workflow-graph-20261012
git pull --ff-only origin feature/job-workflow-graph-20261012
dotnet restore Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj
dotnet build Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-restore
# Set STUDIO_TEST_POSTGRES to your disposable native PostgreSQL connection string.
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --no-build
cd tests/ui
npm ci
npm test
```

Common sibling projects are required, including current Common.Messaging with its email
recipient directory contracts. Native PostgreSQL multi-session concurrency was not tested in
this runtime. The eight database passes use PGlite, a single-session multiplexer. Full media
and storage-backed demo regeneration and a live authenticated Front Desk-to-delivery journey
also require local acceptance. Keep the PR a draft until those checks are completed.

## Existing UI failures

Pristine main e19c5994efc6dc8eddd9097c09337d6db42de4f4: 101 passes and 58 failures.
Integrated feature source: 109 passes and the exact same 58 failure names. The extra eight
passing tests are the graph/editor cases. This comparison isolates the workflow regression
scope; it does not prove these upstream failures are harmless or that all are stale fixtures.
The failing fixture/source expectation checks cover existing shell, routing, legacy flow,
messages, media, scheduling and CSS behavior and remain outside this workflow verification.

- An older Jobs request cannot replace a newer render and opening errors stay inline
- Closed and archived Jobs are hidden by default and can be included or filtered explicitly
- Errors retry; missing workflow does not invent next action
- Finance filters signed records by method and staff without changing their balance
- Finance reconciles signed refunds and discounts with the job balance
- Finance retains completed-job receivables and directs approval to Job Details
- FrontDesk search preserves focus and separates enquiry and booking queues
- Inbox loads one bounded server page instead of fetching every conversation history
- Incomplete attachment upload displays recovery instructions without a routing retry
- Job Details left rail owns actions and respects media edit permissions
- Job filter is sent to the server and preserves the shared Messages page
- Jobs loads real APIs, six columns, 13 rows and filtered total
- Jobs route uses the new component and workspace links without an intermediate page
- Jobs uses its dedicated renderer and shared shell rail
- Known delivery failure retries without uncertain outcome confirmation
- Management workload follows the same schedule resource IDs as Pipeline
- Media collection controls persist names and dropped files target the selected collection
- Media destination job picker selects the real upload destination and filters the tree by status
- Messages opens the inbox, filters real messages, and reads the selected conversation
- Operations decisions link directly to Jobs and exclude self-approval
- Pipeline shows stage groups with schedule and task on timeline and calendar
- Production QC displays the pinned source and records a rejection reason
- Production filters collections and persists completion through the existing API
- Reading pane shows professional identity and a controlled retry for uncertain delivery
- Search keeps focus, clear resets filters, page count is not footer count
- Sort leaves unscheduled last and uses local calendar date
- Status and attention controls filter data; row click and keyboard open directly
- a failed media summary has a working retry without removing sessions and tasks
- a slow Finance response cannot replace a newer Jobs navigation
- administrator can assign a job lead from Today
- all operational right rails keep Messages open at the bottom with a Notes toggle
- cancelled, rejected and reopened stages are not counted as successful passes
- cold workspace opening loads server permissions and keeps actual denial on the landing page
- compact workflow marks the active milestone and exception route
- converted routes keep a single Today board, three information cards and real controls
- corrections and blockers do not mark unvisited future stages complete
- every supported job status has a visible flow light and its current action
- graph connects the main job path and rejoins corrections at the service transition
- grid row highlight and motion have one shared stylesheet owner
- leaving Today aborts enrichment and late replies cannot repaint Pipeline
- main flow stays outside the drawer with status in its original position and actions on the right
- nonshoot work shows photography milestones as not required
- one controller and one stylesheet own message cards without page-specific skins
- reshoot evidence highlights the branch and retains a return to scheduling
- resolved corrections and the completed terminal stage show checks
- shared CSS has one declaration for each selector, property and responsive scope
- shared rail keeps Navigation, My Workspaces and Actions in order
- shell and essential work appear before media; startup never downloads full galleries
- shell sync leaves the router identity intact and browser Back loads one destination
- skipped history gates and their parent phases are checked, but the active shoot is not
- subject View Photos returns to Media and refreshes the subject filter
- successfully passed stages show checks while active and future stages do not
- task list and calendar retain the same scheduled and unscheduled records
- the full shell loads every operational screen with a shared data graph
- upcoming tasks keep the Add task action outside the scrolling task list
- upload selection queues files immediately into the current collection
- workspace job keeps Media dominant with working replies and dedicated panels
- workspace settings follows the active filter and keeps errors inline
