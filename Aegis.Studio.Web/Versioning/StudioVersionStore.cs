using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Versioning;

public sealed class StudioVersionStore
{
    private readonly StudioDatabase database;
    private readonly IFileStorage storage;

    public StudioVersionStore(StudioDatabase database, IFileStorage storage)
    {
        StudioVersionSchema.EnsureCreated(database);
        this.database = database;
        this.storage = new PrefixedFileStorage(
            storage ?? throw new ArgumentNullException(nameof(storage)),
            "VersionStore");

    }

    public IReadOnlyList<StudioAssetVersion> GetHistory(Guid jobId, string assetKey)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note from studio_asset_versions where job_id=$1 and asset_key=$2 order by version_number desc";
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(NormalizeKey(assetKey));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioAssetVersion> result = [];
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public StudioAssetVersion? Get(Guid jobId, Guid versionId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note from studio_asset_versions where job_id=$1 and id=$2";
        command.Parameters.AddWithValue(jobId);
        command.Parameters.AddWithValue(versionId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public StudioAssetVersion Add(Guid jobId, string assetKey, string fileName, string contentType, Stream source, string createdBy, string? note = null, Guid? sourceVersionId = null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        StudioAssetVersion value = Add(connection, transaction, jobId, assetKey, fileName, contentType, source, createdBy, note, sourceVersionId);
        // Public version uploads also register their gallery asset atomically.
        if(!value.AssetKey.StartsWith("unmatched/",StringComparison.Ordinal))
        {
            using var gallery=new NpgsqlCommand("insert into studio_media_galleries(id) values($1) on conflict do nothing",connection,transaction);gallery.Parameters.AddWithValue(jobId);gallery.ExecuteNonQuery();
            using var asset=new NpgsqlCommand("insert into studio_media_assets(id,job_id,gallery_id,asset_key) values(md5($1::text||':'||$2)::uuid,$1,$1,$2) on conflict(job_id,asset_key) do nothing",connection,transaction);asset.Parameters.AddWithValue(jobId);asset.Parameters.AddWithValue(value.AssetKey);asset.ExecuteNonQuery();
        }
        transaction.Commit();
        return value;
    }

    // Callers can enlist version metadata and their workflow state in the same transaction.
    internal StudioAssetVersion Add(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, string assetKey, string fileName, string contentType, Stream source, string createdBy, string? note = null, Guid? sourceVersionId = null)
    {
        LockJobForMediaChange(connection,transaction,jobId);
        string key = NormalizeKey(assetKey);
        Guid id = Guid.NewGuid();
        string stored = $"{jobId:N}/{SafeKey(key)}/{id:N}{Path.GetExtension(fileName)}";
        StoredFile storedFile = storage.StoreAsync(new StorageWriteRequest(
            stored,
            source,
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Path.GetFileName(fileName),
            createdBy,
            new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Application"] = "Aegis.Studio",
                ["JobId"] = jobId.ToString("D"),
                ["AssetKey"] = key,
                ["VersionId"] = id.ToString("D")
            })).GetAwaiter().GetResult();
        // A provider write and SQL commit are separate. Verify readable bytes before publishing metadata.
        using (Stream verified = storage.OpenReadAsync(stored).GetAwaiter().GetResult())
        {
            string actual = Convert.ToHexString(SHA256.HashData(verified));
            if (!actual.Equals(storedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new StorageException("STORAGE-INTEGRITY-001", "Stored upload verification failed.");
        }
        long size = storedFile.Length;
        string hash = storedFile.Sha256;

        // Serialize the version counter per job and asset, including concurrent uploads.
        using (NpgsqlCommand guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = "select pg_advisory_xact_lock(hashtextextended($1,0))";
            guard.Parameters.AddWithValue(jobId.ToString("D") + ":" + key);
            guard.ExecuteNonQuery();
        }
        int version;
        using (NpgsqlCommand next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "select coalesce(max(version_number),0)+1 from studio_asset_versions where job_id=$1 and asset_key=$2";
            next.Parameters.AddWithValue(jobId);
            next.Parameters.AddWithValue(key);
            version = Convert.ToInt32(next.ExecuteScalar());
        }

        StudioAssetVersion value = new(id, jobId, key, version, Path.GetFileName(fileName), string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, size, hash, stored, createdBy, DateTimeOffset.UtcNow, sourceVersionId, note);
        Insert(connection, transaction, value);
        return value;
    }

    internal StudioAssetVersion GetWithinTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid jobId, string? assetKey = null, Guid? versionId = null)
    {
        using var command=new NpgsqlCommand("select id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note from studio_asset_versions where job_id=$1 and ($2::text is null or asset_key=$2) and ($3::uuid is null or id=$3) order by version_number desc limit 1",connection,transaction);
        command.Parameters.AddWithValue(jobId);command.Parameters.AddWithValue((object?)assetKey??DBNull.Value);command.Parameters.AddWithValue((object?)versionId??DBNull.Value);
        using var reader=command.ExecuteReader();return reader.Read()?Read(reader):throw new KeyNotFoundException("Version not found.");
    }
    internal Stream OpenStored(StudioAssetVersion version)=>storage.OpenReadAsync(version.StoredPath).GetAwaiter().GetResult();

    public StudioAssetVersion Restore(Guid jobId, Guid versionId, string createdBy, string? note)
    {
        StudioAssetVersion source = Get(jobId, versionId) ?? throw new KeyNotFoundException("Version was not found.");
        using Stream input = storage.OpenReadAsync(source.StoredPath).GetAwaiter().GetResult();
        return Add(jobId, source.AssetKey, source.FileName, source.ContentType, input, createdBy,
            string.IsNullOrWhiteSpace(note) ? $"Restored from version {source.VersionNumber}" : note,
            source.Id);
    }

    public Stream Open(Guid jobId, Guid versionId, out StudioAssetVersion version)
    {
        version = Get(jobId, versionId) ?? throw new KeyNotFoundException("Version was not found.");
        return storage.OpenReadAsync(version.StoredPath).GetAwaiter().GetResult();
    }

    private static void Insert(NpgsqlConnection connection, NpgsqlTransaction transaction, StudioAssetVersion version)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_asset_versions(id,job_id,asset_key,version_number,file_name,content_type,size_bytes,sha256,stored_path,created_by,created_at_utc,source_version_id,note) values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)";
        object?[] values = [version.Id, version.JobId, version.AssetKey, version.VersionNumber, version.FileName, version.ContentType, version.SizeBytes, version.Sha256, version.StoredPath, version.CreatedBy, version.CreatedAtUtc, (object?)version.SourceVersionId ?? DBNull.Value, (object?)version.Note ?? DBNull.Value];
        foreach (object? value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    internal static void LockJobForMediaChange(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid jobId)
    {
        using var command=new NpgsqlCommand("select status from studio_jobs where id=$1 for update",connection,transaction);command.Parameters.AddWithValue(jobId);
        string status=command.ExecuteScalar() as string??throw new KeyNotFoundException("Job was not found.");
        if(status is "Ready for Delivery" or "Delivered" or "Awaiting Approval" or "Awaiting Production Review" or "Ready for Client" or "Published" or "Completed" or "Closed" or "Cancelled")
            throw new ArgumentException("This job's approved media is closed for changes. Request changes before uploading a revision.");
    }

    private static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Asset key is required.");
        return value.Trim();
    }

    private static string SafeKey(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static StudioAssetVersion Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
        reader.GetInt64(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10),
        reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.IsDBNull(12) ? null : reader.GetString(12));
}

public sealed record StudioAssetVersion(Guid Id, Guid JobId, string AssetKey, int VersionNumber, string FileName, string ContentType, long SizeBytes, string Sha256, string StoredPath, string CreatedBy, DateTimeOffset CreatedAtUtc, Guid? SourceVersionId, string? Note);


