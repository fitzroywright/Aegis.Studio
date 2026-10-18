# Client reminders

Client Reminders is available in navigation and from the job's Assignments & Workflow panel. Users need View Conversation to read reminders, Reply Requester to manage them, and View Financials for deposit reminders.

For a job, confirm the client's channel preferences, then schedule an editable reminder or enable an automatic rule. Both channels and all automatic rules start disabled. Templates support `{client}`, `{job}`, `{detail}` and `{location}`. Manual messages show an editable rendered preview. Automatic template edits apply to future reminders; existing scheduled messages can be edited, postponed or cancelled separately.

Sources and default timing:

- Shoot: 48 hours before the recorded call time. Rescheduling invalidates the old reminder and creates a new one if automation is enabled.
- Deposit: 24 hours after creation of an outstanding booking deposit request. This uses the request's required deposit minus recorded deposits, not a guessed payment deadline or general account balance. A partial payment changes the previewed amount and cancels an existing unsent reminder; staff can schedule a replacement with the current amount.
- Selection / response: 48 hours after a task explicitly enters WaitingOnClient.
- Approval: 48 hours after a WaitingOnClient task whose title identifies an approval. Internal QC and internal review stages never trigger this reminder.
- Collection / delivery: 72 hours after the job becomes Ready for Delivery, provided fulfillment has not been recorded.
- General follow-up: staff-scheduled only, while the job remains active.

The scheduler runs every minute. Before dispatch, the source must still be outstanding, the event anchor, details, location and recipient must match, and the chosen channel must still be permitted. Completed, cancelled, delivered and archived jobs cannot dispatch reminders. Automatic reminders are unique per job/type/source/time, with a limit of three newly scheduled automatic reminders of each type per job in seven days. No indefinite repeat sequence is enabled.

Sending hours are 09:00–18:00 in `ClientReminders:TimeZone` (default `America/Jamaica`). Send now and provider retries also respect these hours. A reminder scheduled outside that window waits until sending hours; its requested scheduled time remains visible. Shoot details explicitly display Jamaica time.

Delivery reuses the existing outbound email/WhatsApp queue. Queue insertion and reminder state changes share a database transaction; the reminder ID is also the outbox ID. Multi-process scheduling is serialized, and outbox dispatch rechecks business state, contact and preferences immediately before claiming a message. HTTP retries reuse the same operation ID.

Configure workspace email identities and the existing email transport as usual. WhatsApp also requires `Messaging:WhatsApp:ReminderTemplateName` and optionally `Messaging:WhatsApp:ReminderTemplateLanguage` (default `en_US`). The approved template must accept one text parameter containing the reminder body. There is no silent channel fallback.

The queue shows scheduled, queued, sending, submitted, failed, attention and cancelled records, plus provider status and errors. Submitted means provider acceptance, not confirmed delivery. Preparation failures can be retried after correcting the problem; the existing transport handles delivery retries. An uncertain or already submitted delivery must be reviewed by a messaging administrator before any resend. Disabling a rule cancels its unsent automatic reminders. Pending dispatch can be cancelled; already started delivery cannot be recalled.

Change history retains reminder snapshots, timestamps and the staff/system actor. Provider progress remains linked through the outbox. Incoming replies use the existing channel routing; this feature does not add a new inbound transport or promise read receipts.

Validation: targeted browser-unit tests cover opt-in defaults, editable previews, stable retry IDs, unavailable events, read-only controls, escaping and queue filters. The SQL migration and business-state predicates are also exercised against a PostgreSQL-compatible local engine. The repository includes .NET unit and isolated PostgreSQL regression tests; full application/provider testing is still required before deployment.
