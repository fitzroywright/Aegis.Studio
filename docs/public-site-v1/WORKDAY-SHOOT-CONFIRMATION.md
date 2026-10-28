# WorkDay photo upload completion

The public WorkDay assignment page now provides **All photos uploaded — finish shoot**. The photographer must explicitly check that every photograph is uploaded and the shoot is finished. A count shows how many of their assignment uploads Studio has imported, across the entire upload history rather than only the visible gallery page.

The button remains unavailable while files are selected locally, an upload needs attention, or Studio has not acknowledged every photograph with a stored gallery version, asset and Job message. The declaration is queued durably; the page says it is waiting for Studio until acknowledged, and retains the result after refresh or sign-in. A conflict displays Studio's reason and allows review/retry. Additional uploads after acknowledgment require another declaration. Uploads cannot be added while a declaration is pending, but retries of already accepted upload operations still work.

`GET/POST /api/v1/workday/assignments/{assignmentId}/shoot-completion` require the signed-in photographer's current assignment. The API captures the complete imported upload-ID set in one SQLite transaction. Completion requests go through the existing WorkDay operation queue as `ConfirmShootComplete`; existing enum values are preserved. The generic public operations endpoint cannot bypass the upload check for this operation.

Studio rechecks the assignment, active photographer, Job state, optimistic version and actual gallery receipts. Imported receipts must belong to this Job, photographer and assignment, with retained versions and active gallery assets. It records the photographer's Completed state and an internal Job message in the same transaction. Existing shoot lifecycle logic marks the shoot `ShootComplete` only when all active assigned photographers finish, and waits for all required shoots before advancing the Job's photography stage. The explicit declaration can be made without inventing arrival/start events or a check-in timestamp. It does not complete the entire customer Job or approve photographs for delivery.

The Job can still have its existing **Awaiting Upload** workflow stage after photography completes; this change does not bypass the normal media review/readiness workflow. The shoot record and internal confirmation message distinguish finished photography from later Job stages.

## Local Mac validation required

No automated test results or .NET compilation are claimed for this draft. JavaScript syntax and whitespace checks were performed during preparation. With the repository dependencies installed, run on the developer Mac:

```bash
node --test tests/ui/workday-web.test.cjs
cd Aegis.Studio.PublicSite
node --test workday-shoot-completion.test.mjs workday-gallery.test.mjs
```

From the repository root, with .NET 10, sibling Common projects and a disposable PostgreSQL database configured through `STUDIO_TEST_POSTGRES`:

```bash
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --filter 'FullyQualifiedName~WorkDayShootCompletionTests|FullyQualifiedName~ManualPhotographerPersistenceTests|FullyQualifiedName~WorkDayPhotoImportTests'
```

Deploy Studio to Kratos before the updated API/public site on AWS. Preserve credentials, databases and photo storage. Confirm a real assigned photographer can upload, wait for every imported file, declare completion, and see the internal Job message and shoot status. Verify two assigned photographers must both finish, pending/failed uploads block confirmation, reload retains the acknowledgment, and conflicts never show success. Existing uploaded photos and attachments are preserved.
