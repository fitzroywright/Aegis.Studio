using Common.Storage;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Workflow;

/// <summary>
/// Irreversible factory reset. Storage is purged before the database transaction;
/// failed object deletions leave database references available for a retry.
/// </summary>
internal static class StudioFactoryReset
{
    private const int PageLimit = 1000;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<object> ExecuteAsync(
        StudioDatabase database, IFileStorage storage, IConfiguration configuration,
        IWebHostEnvironment environment, string? regenerateAs, CancellationToken cancellationToken)
    {
        // A shared bucket/library must never be emptied by a reset operation.
        // This flag must be explicitly configured after verifying the provider root
        // is dedicated to Studio, not just a shared bucket with a Studio prefix.
        if (!configuration.GetValue("Storage:FactoryResetExclusiveRoot", false))
            throw new InvalidOperationException(
                "Factory reset requires Storage:FactoryResetExclusiveRoot=true after verifying the configured storage root is dedicated to Studio.");
        var bucketReset = storage as IStorageBucketReset;
        if (bucketReset is null && (storage is not IStorageQuery || storage is not IStorageLifecycle))
            throw new InvalidOperationException(
                "Factory reset requires dedicated-bucket S3 purge or storage enumeration and irreversible lifecycle purge. No data was deleted.");
        await Gate.WaitAsync(cancellationToken);
        try
        {
            long removed = 0;
            if (bucketReset is not null)
            {
                removed = await bucketReset.PurgeBucketAsync(cancellationToken);
            }
            else
            {
                var query = (IStorageQuery)storage;
                var lifecycle = (IStorageLifecycle)storage;
            var previousBatch = new HashSet<string>(StringComparer.Ordinal);
            // Repeat listing from the beginning: providers have bounded listings
            // and keys already removed will disappear on the next enumeration.
            for (;;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await query.ListAsync(
                    new StorageListRequest(MaximumResults: PageLimit), cancellationToken);
                if (batch.Count == 0) break;
                var keys = batch.Select(item => item.StorageKey).Distinct(StringComparer.Ordinal).ToArray();
                if (keys.Length > 0 && previousBatch.SetEquals(keys))
                    throw new InvalidOperationException("Storage purge made no progress; retry after investigating provider deletion behavior.");
                previousBatch = keys.ToHashSet(StringComparer.Ordinal);
                if (keys.Length == 0)
                    throw new InvalidOperationException("Storage enumeration returned no usable keys.");
                foreach (var key in keys)
                {
                    if (string.IsNullOrWhiteSpace(key) || key.StartsWith("/", StringComparison.Ordinal)
                        || key.Contains("..", StringComparison.Ordinal))
                        throw new InvalidOperationException("Unsafe storage key in reset inventory.");
                    await lifecycle.PurgeAsync(key, cancellationToken);
                    removed++;
                }
            }

            // Re-list after purge; never declare success based on deletion calls alone.
            var remaining = await query.ListAsync(new StorageListRequest(MaximumResults: 1), cancellationToken);
            if (remaining.Count != 0)
                throw new InvalidOperationException("Storage verification failed: Studio files remain.");
            }

            // Local delivery cache is derived from attachments, not user content.
            var cache = Path.Combine(environment.ContentRootPath, "Storage", ".delivery-cache");
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);

            var databaseResult = ConnectedWorkflowDemo.Reset(database);
            object? generated = null;
            if (regenerateAs is not null)
                generated = ConnectedWorkflowDemo.Generate(database, regenerateAs);
            return new { storageObjectsPurged = removed, storageVerifiedEmpty = true,
                database = databaseResult, generated,
                message = "Studio factory reset completed; all enumerable storage objects purged and only Unassigned preserved." };
        }
        finally { Gate.Release(); }
    }
}
