# Products and Services layout and online booking

The selected design is implemented in the existing Products and Services route. The page opens on Services & Sessions and has one tab strip for Products, Services & Sessions and Opening Hours. Session selection opens the inline editor below the grid; the previous details dialog and nested tab strip are removed. Product pricing, tax, package components and work-order templates retain their existing save service. The UI uses actual catalog rows and location hours; no illustrative prices, services or schedules are inserted.

Online Booking switches save immediately through the Administrator-only `PATCH /api/admin/public-booking/session-types/{id}/publication` endpoint. It updates only the selected service's saved publication flag and advances the catalog revision under the same database lock as normal catalog/hour writes. It preserves unrelated local drafts, prevents duplicate submissions, restores the previous visual value on an unconfirmed request and reports errors. New services must be saved first; inactive services must be activated and saved before publication. Catalog and hours saves use `If-Match` revisions so stale drafts cannot overwrite a switch change.

Studio's existing booking publisher reads active, published sessions. After a switch changes the revision, its next configured publication sends the changed catalog and matching availability to the API site; switching the last service off publishes an empty catalog. This requires the existing publishing configuration and scoped integration credential. A local save is not represented as a verified API acknowledgment. The status strip reports configured automatic publication and unsaved drafts, not a fabricated “website up to date” status.

The preview link uses validated `PublicSite:WebBaseUrl`, falling back to `PublicSite:BaseUrl`. Both must be HTTPS origins; no integration credentials are returned to the browser. Optional session categories are stored in the existing JSON catalog payload, with an 80-character limit; no relational schema migration is required. Existing session categories remain unset until edited.

## Verification

Performed during preparation: JavaScript syntax checks for the three changed source files and two changed UI suites; whitespace checks; source review of the save/publisher flow; repository CSS ownership guard against snapshots of the exact affected before/after files (passed). No styles outside the owning commercial stylesheet or shared-shell geometry were changed.

Automated UI/.NET tests, compilation, browser visual comparison, and live publication checks have **not** been executed in this chat. Run on the developer Mac before deployment:

```sh
node --test tests/ui/booking-sessions.test.cjs tests/ui/catalog-bookings.test.cjs
dotnet test Aegis.Studio.Web.Tests/Aegis.Studio.Web.Tests.csproj --filter FullyQualifiedName~PublicBookingPublicationTests
node scripts/check-ui-css-ownership.cjs
```

With the normal .NET 10/Common dependencies and disposable `STUDIO_TEST_POSTGRES` configured, also build Studio. In the browser compare the approved design at desktop width, check smaller widths and themes, edit a session, switch tabs, save hours, and verify a persisted Online Booking on/off change after reload. On a test session confirm the API catalog and available-time list change after the publisher runs, while prices and other unsaved edits remain intact. No AWS or Kratos deployment, restart, secret change or live catalog mutation was performed here.
