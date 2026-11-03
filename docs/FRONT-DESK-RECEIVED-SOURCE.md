# Front Desk received date and source

The Jobs and enquiries grid uses Reference, Client / enquiry, Date Received, Source, Status, Next required action, Responsible and Due. The existing renderer, row actions, highlighting, scrolling and shell remain the owners of this surface.

## Original received date

The protected `/api/frontdesk/intake` projection joins existing conversations to their jobs and pending booking requests. It uses the original conversation's persisted timestamp rather than the appointment, job creation, or last-update timestamp. For email, the earlier of conversation creation and the earliest persisted external message timestamp captures the original incoming email, including delayed ingestion. New email conversations also use the provider's received timestamp directly. Website imports retain their existing submitted `createdAtUtc` timestamp.

Converting an enquiry retains its conversation and therefore the same received date. No historical timestamps are rewritten or backfilled. Missing original metadata displays a dash. The grid shows `DD MMM YYYY` in `Studio:TimeZoneId` (default America/Jamaica); hovering shows the complete timestamp and timezone.

## Business source

The five selectable sources are Website, Walk-In, Phone, Email and Staff Entry. Unknown is an internal fallback and displays as a dash; it never appears in a source selector. Known historical aliases (Online/Web site/WebsiteV1, WalkIn, Manual) normalize to their corresponding business values when read. Unsupported or absent metadata remains Unknown. The historical FrontDesk provider namespace alone does not establish a business source, and an automatically generated Email conversation is not treated as proof of email intake. Persisted numeric booking values retain their positions; the obsolete unsupported value at position 3 becomes Unknown without rewriting any rows.

New pending booking requests persist a separate canonical `source` in their existing JSON payload. The legacy `bookingSource` provider/idempotency namespace is retained. Public website endpoints force Website. Existing signed website intake uses Website for website submissions, Staff Entry for staff mobile submissions and Unknown when the producer's source is unclassified. Manual enquiries and bookings default to Staff Entry; staff can select any supported business source. Phone, Walk-In and Email intake retain their corresponding values. Email ingestion retains Email.

An enquiry conversion always preserves the original enquiry source, including Unknown, even when a caller supplies a different selection. Replayed external booking requests preserve existing account, job, timestamp and provider mappings and do not overwrite their established business source. No external integration was added.

## Filtering, sorting and counts

Received offers Any date, Today, Yesterday, Last 7 days, Last 30 days, This month and a custom inclusive From/To range. Last 7/30 days include the current Studio-local day; This month covers the calendar month. The server applies received-date and source predicates using the configured Studio timezone, including DST boundaries. Custom ranges require both dates and reject reversed bounds. Unknown is not an accepted source-filter choice.

The renderer combines that result with search, location, Responsible, Due, arrival state and the All/Appointments/Enquiries tabs. Date Received defaults to newest first, with oldest first available from the heading or order selector. Existing time/name sorting stays selected across filters and reloads. Filtering and global sorting precede 50-row pagination; footer counts and the enquiry badge use the filtered data.

## Schema and deployment

No database migration or historical data conversion is required. Existing conversation/message timestamps, channel metadata, booking enum values and JSON payload storage are reused. No new dependency or schema table was introduced.

Run local Release builds and PostgreSQL tests using a disposable `STUDIO_TEST_POSTGRES` database. Front Desk UI regressions are in `tests/ui/frontdesk-received-source.test.cjs`, alongside the existing Front Desk fixture and regression tests. Run `node --test tests/ui/frontdesk-*.test.cjs tests/ui/css-ownership.test.cjs tests/ui/shared-shell-ownership.test.cjs` after `npm ci` in `tests/ui`.

The complete existing release suites remain mandatory before merge/deployment. Baseline failures must not be silently skipped or bypassed.
