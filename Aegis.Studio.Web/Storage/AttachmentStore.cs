using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
    private readonly StudioDatabase database;
    private readonly IFileStorage storage;
    private readonly string deliveryCacheRoot;
    private readonly long maximumBytes;

    public AttachmentStore(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        StudioDatabase database)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        string configuredPath = configuration["Storage:AttachmentsPath"] ?? string.Empty;
        string storageRoot = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Storage", "attachments")
            : Path.GetFullPath(configuredPath);
        storage = new LocalFileStorage(storageRoot);
        deliveryCacheRoot = Path.Combine(environment.ContentRootPath, "Storage", ".delivery-cache");
        Directory.CreateDirectory(deliveryCacheRoot);

        int maximumMegabytes = Math.Clamp(
            configuration.GetValue<int?>("Storage:MaxAttachmentMegabytes") ?? 100,
            1,
            1024);
        maximumBytes = maximumMegabytes * 1024L * 1024L;
    }

    public long MaximumBytes => maximumBytes;

    public IReadOnlyList<StudioAttachment> GetForConversation(Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " where conversation_id=$1 order by created_at_utc desc";
        command.Parameters.AddWithValue(conversationId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioAttachment> values = [];
        while (reader.Read())
        {
            values.Add(Read(reader));
        }
        return values;
    }

    public async Task<StudioAttachment> AddAsync(
        Guid conversationId,
        IFormFile file,
        string visibility,
        string uploadedBy,
        CancellationToken cancellationToken,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidOperationException(
                $"Attachment must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        }

        await using Stream source = file.OpenReadStream();
        return await AddStreamAsync(
            conversationId,
            file.FileName,
            file.ContentType,
            source,
            visibility,
            uploadedBy,
            cancellationToken,
            correlationId: correlationId);
    }

    public async Task<StudioAttachment> AddBytesAsync(
        Guid conversationId,
        string fileName,
        string contentType,
        byte[] bytes,
        string visibility,
        string uploadedBy,
        CancellationToken cancellationToken,
        string? sourceKey = null,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length <= 0 || bytes.LongLength > maximumBytes)
        {
            throw new InvalidOperationException(
                $"Attachment must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        }

        await using MemoryStream source = new(bytes, writable: false);
        return await AddStreamAsync(
            conversationId,
            fileName,
            contentType,
            source,
            visibility,
            uploadedBy,
            cancellationToken,
            sourceKey,
            correlationId);
    }

    public async Task<StudioAttachment> AddStreamAsync(
        Guid conversationId,
        string fileName,
        string contentType,
        Stream source,
        string visibility,
        string uploadedBy,
        CancellationToken cancellationToken,
        string? sourceKey = null,
        string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? normalizedSourceKey = string.IsNullOrWhiteSpace(sourceKey) ? null : sourceKey.Trim();
        if (normalizedSourceKey is not null)
        {
            StudioAttachment? existing = GetBySourceKey(normalizedSourceKey);
            if (existing is not null) return existing;
        }
        if (!source.CanRead)
        {
            throw new InvalidOperationException("Attachment source stream must be readable.");
        }

        string originalName = NormalizeFileName(fileName);
        string normalizedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();
        string normalizedUploadedBy = string.IsNullOrWhiteSpace(uploadedBy)
            ? "Unknown"
            : uploadedBy.Trim();
        Guid id = Guid.NewGuid();
        string storageKey = BuildStoredName(id, originalName);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Application"] = "Aegis.Studio",
            ["ConversationId"] = conversationId.ToString("D"),
            ["Visibility"] = NormalizeVisibility(visibility)
        };
        if (!string.IsNullOrWhiteSpace(correlationId))
            metadata["CorrelationId"] = correlationId.Trim();

        StoredFile stored = await storage.StoreAsync(
            new StorageWriteRequest(
                storageKey,
                source,
                normalizedContentType,
                originalName,
                normalizedUploadedBy,
                metadata,
                maximumBytes),
            cancellationToken);

        StudioAttachment attachment = CreateAttachment(
            id,
            conversationId,
            originalName,
            storageKey,
            normalizedContentType,
            stored.Length,
            visibility,
            normalizedUploadedBy,
            stored.Sha256);

        try
        {
            InsertAttachment(attachment, normalizedSourceKey);
            return attachment;
        }
        catch
        {
            await RollBackStoredAttachmentAsync(storageKey, cancellationToken);
            throw;
        }
    }

    public StudioAttachment? Get(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public Task<Stream> OpenReadAsync(
        StudioAttachment attachment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return storage.OpenReadAsync(attachment.StoredName, cancellationToken);
    }

    public string GetPhysicalPath(StudioAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        string cachePath = Path.Combine(deliveryCacheRoot, attachment.StoredName);
        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        using Stream source = storage
            .OpenReadAsync(attachment.StoredName)
            .GetAwaiter()
            .GetResult();
        using FileStream destination = new(
            cachePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        source.CopyTo(destination);
        destination.Flush();
        return cachePath;
    }

    public Task<StorageHealth> CheckStorageHealthAsync(
        CancellationToken cancellationToken = default)
        => storage.CheckHealthAsync(cancellationToken);

    private async Task RollBackStoredAttachmentAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (storage is IStorageLifecycle lifecycle)
        {
            await lifecycle.PurgeAsync(storageKey, cancellationToken);
            return;
        }

        await storage.DeleteAsync(storageKey, cancellationToken);
    }

    private static string NormalizeFileName(string fileName)
    {
        string originalName = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(originalName))
        {
            throw new InvalidOperationException("Attachment file name is required.");
        }
        return originalName;
    }

    private static string BuildStoredName(Guid id, string originalName)
    {
        string extension = Path.GetExtension(originalName);
        return id.ToString("N") + extension.ToLowerInvariant();
    }

    private static string NormalizeVisibility(string visibility)
        => string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase)
            ? "Internal"
            : "External";

    private static StudioAttachment CreateAttachment(
        Guid id,
        Guid conversationId,
        string originalName,
        string storedName,
        string contentType,
        long size,
        string visibility,
        string uploadedBy,
        string sha256)
        => new(
            id,
            conversationId,
            originalName,
            storedName,
            contentType,
            size,
            NormalizeVisibility(visibility),
            DateTimeOffset.UtcNow,
            sha256,
            uploadedBy,
            "Stored");

    private void InsertAttachment(StudioAttachment attachment, string? sourceKey)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_attachments(id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status,source_key) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)";
        command.Parameters.AddWithValue(attachment.Id);
        command.Parameters.AddWithValue(attachment.ConversationId);
        command.Parameters.AddWithValue(attachment.FileName);
        command.Parameters.AddWithValue(attachment.StoredName);
        command.Parameters.AddWithValue(attachment.ContentType);
        command.Parameters.AddWithValue(attachment.Size);
        command.Parameters.AddWithValue(attachment.Visibility);
        command.Parameters.AddWithValue(attachment.CreatedAt);
        command.Parameters.AddWithValue(attachment.Sha256);
        command.Parameters.AddWithValue(attachment.UploadedBy);
        command.Parameters.AddWithValue(attachment.StorageStatus);
        command.Parameters.AddWithValue((object?)sourceKey ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private StudioAttachment? GetBySourceKey(string sourceKey)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " where source_key=$1";
        command.Parameters.AddWithValue(sourceKey);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private const string SelectSql = "select id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status from studio_attachments";

    private static StudioAttachment Read(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            reader.IsDBNull(9) ? "Unknown" : reader.GetString(9),
            reader.IsDBNull(10) ? "Stored" : reader.GetString(10));
}

public sealed record StudioAttachment(
    Guid Id,
    Guid ConversationId,
    string FileName,
    string StoredName,
    string ContentType,
    long Size,
    string Visibility,
    DateTimeOffset CreatedAt,
    string Sha256,
    string UploadedBy,
    string StorageStatus);
