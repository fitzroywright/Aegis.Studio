# Demo photo quality correction — October 12, 2026

The previous catalogue mostly used political portraits, sometimes at 1280 pixels, and the downloader randomly chose a RandomUser profile-picture fallback. It then capped stored images at 1200 pixels. This produced unsuitable Studio galleries.

## Changed behavior

- Embedded reviewed Pexels pools: 46 wedding, 41 family, 46 graduation, 40 portrait and 43 corporate entries (216 categorized entries, 212 unique source URLs; four photographs are intentionally useful in two categories).
- Every included URL was requested directly and its actual JPEG dimensions inspected. Rejected 404s, undersized originals and sources exceeding Studio's 60-million-pixel ceiling. Source page and license remain attached to every catalogue entry.
- Select sources by Job type and stable per-Job shuffle. Fresh full-demo jobs get 40–50 distinct source photographs, bounded by their matching pool. This replaces the old inflated 100–241-image fresh-gallery counts. Existing stored larger galleries are preserved during non-reset repair.
- The legacy/top-up phase excludes already-used source URLs before selecting new photos, so it does not fill forty rows with repeated source images.
- Remove RandomUser and the 1200-pixel cap. Require at least 2400 pixels on the original's longest edge. JPEG source bytes are stored unchanged; other raster formats are encoded as full-dimension JPEGs. Normal Media derivative generation remains separate.
- Keep source originals in a reusable local disk cache outside the reset S3 bucket, with a bounded memory cache. Optional environment variable: DemoData__Photos__CachePath. Default: the service user's temporary directory / aegis-studio-demo-originals-v2. Cache images are checked before reuse; invalid cache files are removed and downloaded again.
- Retain source/page/license and original-byte integrity checks in Common.Storage. Enforce 100 MB and 60 million pixel limits and a body-read timeout. Photo failures are explicit; no small substitute or unrelated random provider is used.
- No Pexels API key is required. Runtime consumes the embedded reviewed source list, not site search or scraping. Adding a photo requires reviewing its people content, license and full-size dimensions before adding it to the matching pool.

## Verification

- Checked original HTTP responses and JPEG dimensions for all 216 included entries; every category has at least forty distinct URLs.
- Downloaded and decoded ten complete JPEG originals (two per category), and visually inspected a contact sheet. Samples show real weddings, families, graduates, portraits and business groups.
- Parsed all seven changed C# source/test files with the C# grammar: no syntax error nodes. This is not compilation or execution.
- Updated regression coverage for category identity, unique sources, stable retry ordering, minimum/maximum dimensions and fresh gallery counts.
- No .NET SDK is installed here: local build, .NET test execution, PostgreSQL persistence and a real Kratos/S3 reset remain pending.

## Local acceptance

Pull the branch and run the normal restore/build, then:

~~~powershell
dotnet test Aegis.Studio.Web.Tests --filter 'FullyQualifiedName~PhotoCatalogueHasDistinctRelevantHighResolutionPools|FullyQualifiedName~FullDemoRegenerationTests'
~~~

Use disposable PostgreSQL via STUDIO_TEST_POSTGRES for reset/regeneration tests. After deploying, run Reset and Regenerate to replace existing poor demo photographs. Generate alone preserves existing valid stored records and will not automatically replace all old images. Confirm at least forty photographs per Job, correct source categories, full-size dimensions, derivative previews, progress and retry behavior. No deployment or external reset was performed by this change.
