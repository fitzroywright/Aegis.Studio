# Wedding lifecycle: FrontDesk and workspace/job review

## Implemented in this change

FrontDesk now has separate Booking requests, Enquiries, Active jobs, Delivery / balance, and Completed queues. Search updates only the rows, preserving keyboard focus. Pending requests contain the destination workspace, customer, event date, required deposit and recorded deposit total. Financial detail remains in FrontDesk.

Use **New booking request**, or **Prepare booking request** on an unassigned enquiry. Creating the request does not create a job or reserve a schedule slot. **Mark deposit requested** records that staff already contacted the customer; it sends no message. **Record verified deposit** records money staff have already received and checked; it does not charge a card or verify a gateway transaction. The current deposit ledger supports JMD.

Partial deposits keep the request awaiting deposit. Once the threshold is met, one database transaction creates the conversation (or reuses the enquiry), job, internal handoff note and deposit financial events, and marks the request Confirmed. The job starts at **Needs Scheduling**. Same provider/reference/amount/currency retries return the existing result; conflicting reuse is rejected. Request row locks serialize different deposits; receipt locks serialize receipt replay across requests. After confirmation, further payments go through existing job Billing / POS. Promotion of a conversation with a pending request is blocked on the ordinary create-job endpoint.

Job pages retain the conversation and Gallery, Subjects, Tasks / Calendar, Activity tabs. A compact handoff line shows the next operational step, actual shoot completion, open/blocked tasks and gallery assets. It does not infer approval or completion from uploaded files. Authorized managers can schedule directly from the job, checking the same photographer availability records used by WorkDay plus resource conflicts. Scheduling updates an unscheduled job to Scheduled and assigns its first photographer. My Work now includes WorkDay shoots as well as tasks.

## Evaluation against the requested journey

| Step | Current evidence and remaining work |
|---|---|
| Website → structured email → Hub → FrontDesk | Email ingestion exists; a Hub adapter that validates structured wedding requests and routes them into the new pending-request API is still needed. This change does not claim the public website is connected. |
| Pending booking before job | Implemented for the new FrontDesk request path. Existing `/api/bookings` remains a legacy path that creates a job early; external callers must be migrated before claiming the full journey is aligned. Existing bookings are preserved. |
| Deposit request | Staff can record that a request was sent. Automated deposit communications through Hub are pending. |
| Receipt → booking confirmation → job | Implemented for manually verified deposits with partial payment handling and receipt replay protection. Automated gateway verification is pending. |
| Pipeline → Needs Scheduling | New confirmed requests create jobs with that exact status, already included in the Pipeline board. |
| Manager checks WorkDay availability and assigns photographer | New job scheduling action uses existing WorkDay availability records and conflict checks. The saved shoot reserves the photographer resource, updates the job and uses existing assignment access/notification code. |
| Photographer sees work | My Work includes WorkDay bootstrap assignments; WorkDay already reads shared schedule/resource records. |
| Customer confirmation | Existing schedule notifications are requested by the scheduling endpoint. Their actual delivery still depends on configured providers/preferences. This is not a Hub delivery guarantee. |
| Finish shoot → Awaiting Upload timer | Shoot checkout exists, and WorkDay has its own operation state. A unified finish-shoot event and durable upload-to-review timer remain pending. No fabricated elapsed timer is displayed. |
| Upload → wedding collections → versions | Job gallery versions, source notes and named collections already work. WorkDay uploads currently enter conversation attachments; promoting these to curated job assets remains pending. |
| Submit prepared versions for review → stop timer | A durable submission manifest containing exact version IDs, designated reviewers, timer stop and notification delivery remains pending. |
| Per-photo changes → revision tasks → resubmit → approve | Tasks and versions exist independently; review decisions linking exact photos/versions to generated tasks and resubmissions remain pending. |
| Dispatch approved work to Production | Current activities/handoffs exist. Approval-gated dispatch and a queue containing only dispatched work are pending. |
| Explicit client publishing | Legacy workspace gallery publication exists. Job-level immutable curated manifests and a provider-neutral Publishing API are pending. New uploads are not auto-published by these changes. |
| Client selections / approval / downloads | Legacy gallery access and selection features exist; binding permissions and decisions to the new published job manifests remains pending. |
| Remaining payment and delivery | FrontDesk now exposes a Delivery / balance queue with job billing access. A complete order balance and required-deliverables completion gate are pending. |
| Accounting handoff | Existing accounting components remain; the new manual deposit ledger records financial facts but does not claim export, reconciliation or acknowledgement. |
| Completed → business insights | Existing job status/task metrics remain. End-to-end metrics require the missing review, publish, accounting and completion events. |

## Validation and next acceptance run

JavaScript tests cover queue behavior, search focus, retained job tabs/reply behavior and evidence-based handoffs. C# tests cover deposit thresholds and invalid amounts. The PostgreSQL test creates its own temporary schema, checks partial deposits, concurrent receipt replay, concurrent distinct receipts, one job creation and rejection of cross-request receipt reuse. It runs only when `STUDIO_TEST_POSTGRES` points to a disposable test database; otherwise it is explicitly skipped.

Local test path: create a wedding request with a JMD 100 required deposit, record JMD 40, verify no job, then record JMD 60 under another reference. Verify one job in Needs Scheduling, replay that second receipt and verify unchanged totals. Schedule an active photographer with a linked resource and compare My Work/WorkDay. Verify a blocked availability interval cannot be scheduled. Do not use production payment records for this acceptance run.

Next implementation priority is the Hub intake/communication adapter and unified finish-shoot/review state machine, followed by approval-gated production, curated publishing and completion/accounting gates.

Verification in this environment: 10 UI tests passed; isolated .NET compilation and all 10 C# test cases passed, including actual store SQL through Npgsql against PGlite. PGlite multiplexes connections, so this does not establish native PostgreSQL concurrency behavior. The committed database test should also run against native PostgreSQL via `STUDIO_TEST_POSTGRES`. Full solution build and browser visual verification were not available here.
