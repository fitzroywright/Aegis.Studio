# Studio demo reset

Studio Management offers Generate Demo Data and Reset and Regenerate. These require an authenticated Studio Administrator. Outside Development, set DemoData__Enabled=true.

DemoData__AllowDestructiveReset is no longer required or checked.

Reset and Regenerate requires the confirmation phrase REMOVE STUDIO DEMO DATA and Storage__FactoryResetExclusiveRoot=true. The configured bucket or filesystem root must be dedicated to Studio.

The reset validates database cleanup before deleting files. For S3, Studio uses Common.Storage.IStorageBucketReset.PurgeBucketAsync, which purges the dedicated bucket including orphan objects outside the logical prefix. For filesystem providers, Studio enumerates keys, permanently purges each key and verifies the listing is empty. A purge failure stops the database reset; storage deletion may already be partial.

After storage purge, the reset clears Studio business data and preserves Unassigned, staff/security/configuration and the active progress record. It then runs the full demo generator: jobs, schedules, clients, messages, payments, eight Job Templates, ten graph scenarios, two Work Order Templates and at least 40 people photographs for every demo job.

Generate Demo Data runs the full generator without purging storage or resetting the database. It reuses existing seed data.

## October 12 storage capability correction

Common.Storage S3FileStorage exposes IStorageBucketReset, not IStorageLifecycle. Studio previously required IStorageLifecycle even though its error message advertised dedicated-bucket purge. The corrected dispatcher accepts and calls IStorageBucketReset while retaining the filesystem path.

Update Common.Storage/main alongside Aegis.Studio before building and deploying. No manual TrueNAS purge is required for the S3 provider after deploying the correction. The legacy AmazonS3 adapter does not expose bucket reset; use the S3 provider configuration.

Four focused regression tests cover bucket-only capability acceptance and dispatch, cancellation forwarding, exclusive-root refusal, unsupported-provider refusal and failure propagation. The .NET SDK is unavailable in this editing environment; these tests have not been executed here. Run locally:

~~~powershell
dotnet test Aegis.Studio.Web.Tests --filter FullyQualifiedName~FactoryResetStorageTests
~~~

Live S3 reset/regeneration remains a deployment acceptance check. No external bucket purge or deployment was performed by this change.
