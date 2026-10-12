# Messages, Pipeline, Operations and Media

Base: main 7884d31b9f88ac2945d7ac8f4b413cbf6e0c1a52, including demo-people-photo PR #121.
The isolated source originally matched all 483 blobs at main 7291ddc; upstream PR #121
was incorporated without including its changes in this feature's diff.

## Final behavior

- Messages uses a side-by-side bounded server inbox and conversation reader, with
  recipient/sender/delivery context. Search, job filtering, pagination, direct/legacy
  threads, mentions, attachments, professional identity, retries and realtime handlers remain.
  The previously unlinked chat/composer/dialog stylesheet is loaded from css/components/messaging.css.
- Pipeline defaults to a compact Jobs grid. Timeline/calendar, dated/undated tasks,
  schedule editing, resources and drag/drop remain. Workspace/search/status controls
  use All Open Jobs by default; explicit status selection can retrieve closed jobs.
  Stage counts use the filtered dataset. Next opens the actual Assignments & Workflow
  panel; Move opens its versioned workspace-transfer action. Removed obsolete direct
  status writes: the current backend explicitly rejects them.
- Operations shows workspace addresses, open-job counts, exception counts and job
  responsibility. Selecting a workspace filters its jobs and displays real context.
  Missing email is flagged except for Unassigned. Manager-only deposit decisions,
  self-approval exclusion and website delivery monitoring remain as separate sections.
- Media retains the actual tree, scalable job/subject pickers, file/status filters,
  sort, carousel, refresh, destination tagging and Save Tags. Upload sits above the
  library; the compact selected-image card sits to the right. The card adds the real
  full preview/fallback and original-content link; subjects and collections remain
  editable through the existing asset API.
- Media upload is aligned with the current backend batch/receipt contract: declare
  the file manifest first, then upload each file using its stable upload ID. Multiple
  shoot sessions require an explicit selection; one session is selected automatically.
  Failed/uncertain uploads retain file objects and the batch/receipt IDs for Retry Upload,
  preserving the original destination and avoiding duplicate stored versions. RAW files
  and documents are accepted. A pending failed batch must be retried before choosing a
  new batch. Pending file objects are kept only for the current page instance; do not
  navigate away during recovery. Session permissions remain enforced by the server.

The Today shell, navigation, header, profile menu and footer remain. Operations now
has the correct masthead name. Page styles own their rules; retired Media/inbox rules
were removed from the legacy stylesheet rather than overlaid. Shared surface/text
rules use semantic tokens for Standard, Light and Dark.

## Validation

- 25 focused UI checks passed, including upload declaration/session/tagging/retry,
  original/RAW fallback URLs, carousel/filtering, delivery retries and reply attachment
  request IDs, manager-only decisions, closed-job lookup and Pipeline workflow delegation.
- Six existing .NET declaration/validation tests passed against the prepared local build.
  The stored-version persistence test skipped because no test PostgreSQL connection was set.
- Chromium: all four pages at 1440x900 and 1366x768 in Standard, Light and Dark
  (24 combinations), no document overflow or page exceptions. Save Tags remains visible;
  browser file-input upload reaches the batch and receipt endpoints.
- Full UI suite: 133 passed, 48 failed, 181 total. All 48 failing test names also fail
  on the starting main baseline (116 passed, 58 failed). No new failing test names.
  Relevant stale test fixtures now load their actual shared dependencies and match current
  permission/status contracts. Existing CSS ownership and unrelated production/finance
  fixture failures remain; the full suite is not a green deployment gate.
- JavaScript syntax, stylesheet existence, canonical stylesheet paths and git diff checks passed.

No Kratos deployment, production writes, real email sends, data reset or S3 upload was performed.
The browser uses controlled API responses. Live Kratos/S3 upload and native PostgreSQL
receipt acceptance remain deployment checks. No server data model or provider was modified.
