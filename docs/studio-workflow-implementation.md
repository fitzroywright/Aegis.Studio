# Studio workflow implementation

## Product contract

Navigation: Today, Dashboard, Workspaces, Pipeline, Production, FrontDesk, WorkDay, Admin.

Workspaces contain announcements and jobs. They are not general message queues. A job has its conversation above Gallery, Subjects, Tasks/Calendar, and Activity. Details is a small collapsible card. Subjects are the people being photographed, distinct from the purchasing client; one subject renders as a card, multiple subjects as a roster. Tasks and Calendar are views of the same task records.

Gallery assets belong to jobs, grouped into collections, with immutable versions and provenance. Existing conversation attachments remain available separately. Client galleries are curated, explicitly published snapshots, never automatic views of every internal upload.

FrontDesk owns clients, sales, bookings, deposits and payments. Site bookings arrive through Aegis Hub. Confirmed deposit payment triggers job creation exactly once. Pipeline manages work from needs scheduling through delivery, using WorkDay availability. Confirmation updates the client and WorkDay. Production is a queue of dispatched work, not chat.

Finish shoot starts the upload timer. Submit for review stops it; first upload does not. Reviews can request changes, retaining version history and allowing resubmission. Job milestones and Hub communications appear in the job conversation. Publishing uses a provider-neutral API for Bryan's public site. Dashboard starts with insights from existing data and deepens as integrations arrive. Admin owns users, workspace membership and roles.

## Iteration 1 implemented

- Eight navigation destinations, reusing FrontDesk, WorkDay management and current administration.
- Workspace Jobs, Announcements and Members; removes enquiry and general-message tabs from the workspace UI. Legacy messages remain stored.
- Additive announcement and subject tables; endpoints enforce workspace/job permissions.
- Subject card/roster with add/edit.
- Four job tabs and collapsible Details.
- Tasks and a monthly calendar use the same operational activities; unscheduled tasks stay visible.
- Activity uses the existing operational audit.
- Job gallery lists latest asset versions, collections, history, source notes, uploader, timestamps and hashes. Conversation attachments remain accessible.
- Existing version endpoints reused (registered by Production); version schema initialized before gallery queries, and per-asset upload serialization prevents duplicate version numbers.
- Pipeline navigation has a read-only board of existing statuses and access to existing scheduling. This is a foundation, not the new scheduling workflow.
- Today filters existing tasks for the signed-in user; Dashboard reuses current operational insights.

## Iteration 2: Pipeline and execution

Introduce explicit stage transitions and migration from legacy status strings. Use WorkDay availability when scheduling, confirm client and WorkDay consistently, and dispatch production work. Preserve existing scheduling and mobile capture code. Add a review state machine with requested changes, versioned review submissions and resubmission. Record finish-shoot and submit-for-review events; compute upload time only from those events. Append idempotent milestone entries to job conversations. Replace the broad current Production screen with the dispatched queue.

## Iteration 3: FrontDesk and Hub handoff

Keep existing FrontDesk/POS code. Add the Hub inbound booking contract and persistent idempotency keys. Store bookings before jobs. A verified deposit event creates or returns the single job in a transaction; never create a job from an unverified client callback. Preserve legacy booking/job links. Route event-driven communications through a durable Hub outbox, with correlation/delivery state represented in the job conversation.

## Iteration 4: Publishing and insights

Define a provider-neutral publishing contract and adapter for Bryan's site. Publish only explicitly selected, approved asset version IDs as an immutable curated manifest; support unpublish and audit. Never publish new uploads implicitly into an existing publication. Keep old workspace gallery publishing separate until migrated. Add operational insights (aging, blocked jobs, overdue work, review turnaround, upload elapsed time) from actual recorded events; do not infer nonexistent events. Financial detail stays in FrontDesk.

## Verification gates

Test per-job isolation, announcement posting roles, subject edits, identical task/calendar records, simultaneous version uploads, immutable originals, deposit replay, scheduling conflicts, review resubmission, timer boundaries, Hub retries and publish consent. Iterations 2–4 are pending and are not represented as completed by the first-stage UI.
