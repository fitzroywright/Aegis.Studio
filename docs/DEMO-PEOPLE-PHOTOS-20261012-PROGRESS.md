# People photographs for every demo job

Base main: 7291ddc76514e4747f6f3596839f58cd1a21f383.

Every generated demo job, including 126 dense scheduling jobs, is topped up to at least
40 stored photo assets. Existing larger school/graduation galleries are retained.
The photo phase commits one job at a time and reruns reuse stable asset/version IDs,
check stored bytes, and preserve existing asset edits. Completion follows this phase.

Additional photos use the genuine Commons portrait catalogue with RandomUser portrait
fallback. Rich jobs now also use people portraits instead of product photography.
Unrestricted keyword image downloads were removed to avoid scenery/cartoon results.
Sources and image bytes are persisted through the existing verified VersionStore path.
Source photographs may be reused between jobs; the minimum counts refer to assets per job.

Verification complete: clean Web/Web.Tests build, zero warnings/errors; 307 non-database
tests passed (42 database skips). Both embedded PostgreSQL reset/regeneration checks passed
in 1 minute 38 seconds. Every generated job has at least 40 assets, the 180-photo school
gallery survives, and a second photo pass does not duplicate assets.
The persistence check uses a genuine cached photo fixture; public hosts/S3 remain deployment checks.

Pull main and redeploy, then use Studio Management > Demo Data > Reset and Regenerate.
Generate Demo Data can also top up an existing demo without clearing it.
