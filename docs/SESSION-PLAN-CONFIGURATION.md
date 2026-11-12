# Configurable Session Plans

Session Plan is a job workspace with categories, ordered grouping levels, stable subject identities, named required photograph types and per-subject photo review. It is not a school-specific model. Categories, hierarchy labels and values, roles and required shot names are stored in each existing subject's `attributes.sessionPlan`; no new database tables are required.

Examples:

| Job | Category | Hierarchy | Example requirements |
| --- | --- | --- | --- |
| School | Students | Grade → Class | Portrait, Full length, Creative pose |
| School | Staff | Department | Headshot, Formal portrait |
| Wedding | Wedding party | Party → Family | Formal portrait, Informal portrait |
| Campaign | Products | Range → Item | Front, Detail, Lifestyle |
| Team | Participants | Region → Venue → Team | Programme portrait, Action photograph |

All categories, level names and shot names above are examples. Nothing in the workspace switches behavior based on those names. Class, department and family photographs are their own subjects with stable references and separate requirements; an individual photograph never completes a group deliverable.

## Using the workspace

Open Session Plan from a job. Categories, hierarchy filters, group schedules and the paged roster share one workspace. Select a person to open their required photographs and their full photo library. Search includes stable references, so two people with the same name remain distinct.

Use **Edit structure / required shots** on a subject to define a category, ordered hierarchy, optional role, identity confirmation and up to twenty named required shot types. Each type may reference multiple photos assigned to that exact subject. Confirmed identity plus a selected, existing photograph version is required for approval. Changing approvals requires ManageProduction as well as EditJob. Deleting or detaching an approved image makes its requirement await review when the plan reloads; the approval is never inferred from upload counts. Absent/photographed outcomes remain field reporting, independent of shot approvals.

Select roster rows and use **Structure / shots for selected** to apply a category or hierarchy and add required shots. Blank fields keep each person's existing structure. Existing shot assignments and approvals are preserved. Partial saves are reported, and retry skips subjects already saved. Close without retrying and reload to obtain fresh revisions after a conflict.

Use **Plan Selected** to set planned arrivals. **Assignments & Schedule** remains the authoritative staff/resource reservation workflow. Planned arrivals do not reserve photographers or studios.

## Import and export

CSV and XLSX mapping retain the existing stable-ID matching, ambiguity review, unmapped fields, contacts, scheduling permission checks and concurrency revision checks. The internal Studio importer additionally maps **Category** and **Group path**:

```csv
name,reference,collection,planCategory,planGroups
Amara Williams,SCH-00714,7B,Students,Grade: Grade 7 / Class: 7B
Amara Williams,SCH-00728,7B,Students,Grade: Grade 7 / Class: 7B
Simone Reid,STF-0018,Mathematics,Staff,Department: Mathematics
Class 7B photograph,CLASS-7B,Class groups,Group photos,Grade: Grade 7 / Class: 7B
```

Hierarchy syntax is `Level: Value / Level: Value`, with up to eight uniquely named levels. Export uses a JSON array of name/value objects to preserve punctuation and separators exactly; the importer accepts that representation too. Stable references cannot be silently replaced or dropped. Category and hierarchy imports do not alter approvals. The shared roster parsing engine exposes these fields only to Studio's Session Plan importer, not to the public website's existing roster editor.

## Demo generation

The existing administrator demo recreation flow now runs `DemoSessionPlans.Seed` after photograph population and before final validation. Photo population, Session Plan seeding and final photo validation use the same `DemoJobScope`: demo job IDs, demo conversation IDs, explicitly seeded Front Desk requests and demo booking references. A job produced by normal conversion can have a random ID and still retain its demo provenance. Unrelated jobs are excluded from these three operations. Every selected job receives configurable subjects, group deliverables and required photographs. Final validation rejects a generated demo job with no required shots.

The first school example includes 1,200 new fictional students across six grades and forty classes, 96 new fictional staff across eight departments, forty class group subjects and eight department group subjects. Existing photographed school subjects are retained as additional participants; their IDs and photograph links are not replaced. Other jobs use smaller school examples, wedding parties, families, corporate teams or product ranges as appropriate.

New subjects intentionally start without fabricated photo associations. Existing photographed subjects receive requirement examples backed by their own actual linked files and versions, including pending review, approval and retake examples. Demo identity confirmation and approvals are fictional demonstration data.

Deployment does not automatically recreate the demo dataset. Use the existing administrator demo recreation control when a full reset is intended; it clears and recreates the demo dataset. No live reset is performed by this code change.

## Verification and deployment

Run the Mac's established `Aegis.Operations/scripts/Build-Deploy-Studio-Kratos.ps1` workflow with tests enabled. Also run the UI tests in `tests/ui` and the SubjectPlanRulesTests/SubjectPlanStoreTests tests. The latter require the existing disposable `STUDIO_TEST_POSTGRES` fixture for database checks. Browser acceptance should exercise nested groups, same-name people, multiple images per required shot, a stale revision, read-only access, partial bulk-save retry, large roster paging and demo regeneration.

The implementation session ran JavaScript behavior and stylesheet ownership checks. The execution environment had no .NET SDK or PostgreSQL fixture, and browser rendering was unavailable. Compilation, native/database tests, visual acceptance and Kratos deployment must be completed on the Mac/deployment environment.

## Reusable Session Plan templates

Admin → Session Plan Templates is the single template management page. Create a blank
structure or choose Browse examples and copy an example into the same editor. Example
school, wedding, corporate and product content does not define any special application
branches. Blank templates have no categories or levels.

The editor supports arbitrary category names; Individual, Group and Item subject kinds;
up to eight ordered grouping levels per category; additional identifying fields; and up
to twenty named photo requirements per category. Photo requirements have instructions
and a Required flag. Categories, levels, fields and requirements can be added, renamed,
removed and reordered using the up/down controls. Stable subject reference and name
remain mandatory, independently of additional identifying fields.

Set availability to all workspaces or selected workspaces. Deactivate a template to
remove it from future job selections. Duplicate creates a separate, initially inactive
copy. Template writes check the expected revision; conflicting edits retain the form.

From a job's Session Plan choose Apply template, review the structure and apply it, or
choose Start blank. Available templates are restricted to active templates that include
the job's workspace. The server rechecks availability and both preview/template and job
structure revisions when applying. Template changes never update existing job copies.
Customize structure edits only that job's copy.

Applying or changing a structure preserves all existing subjects, photos, requirements
and approvals. The copied category requirements initialize new subjects created through
the manual-entry or staff roster-import path. For imports into a multi-category plan,
map `planCategory`; `planGroups` uses that category's ordered grouping level names.
Existing subjects continue to use their own requirements, editable individually or with
the existing selected-subject structure/shot tools. This avoids retroactively replacing
reviewed work. Custom identifying values are entered in Photos / requirements → Edit
structure / required shots. Required identification fields must be completed before
confirming identity. Their definitions are copied per subject and cannot be removed by
an incoming subject-save payload. Optional photographs do not block required coverage.

Persistence uses `studio_session_plan_templates` and `studio_job_plan_structures`, created
idempotently during normal database initialization. Job structures contain independent
JSON snapshots. Template writes require Administrator; job structure writes and template
selection require EditJob; reading a job structure requires ViewJob. Existing subject and
production approval permissions remain enforced. Deployment does not recreate demo data.

Validation for this change: focused JavaScript/DOM and canonical CSS ownership checks
pass (74 tests); the whole UI run passed 554 with 19 existing failures before updating
the two stale Admin navigation expectations. Those two now pass in the focused run.
Native .NET/PostgreSQL tests were added for generic validation, copied requirements,
identification enforcement, revision conflicts, scope and snapshot independence. The
.NET SDK/PostgreSQL are unavailable in the editing environment; run the normal Mac
build/deployment script without SkipTests. Browser visual comparison remains pending.
