using Common.Storage;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
    private readonly StudioDatabase database;
    private readonly IFileStorage storage;
    private readonly long maximumBytes;

    public AttachmentStore(IWebHostEnvironment environment, IConfiguration configuration, StudioDatabase database)
    {
        this.database = database;
        string configuredPath = configuration["Storage:AttachmentsPath"] ?? string.Empty;
        string attachmentDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Storage", "attachments")
            : Path.GetFullPath(configuredPath);
        storage = new LocalFileStorage(attachmentDirectory);
        int maximumMegabytes = Math.Clamp(configuration.GetValue<int?>("Storage:MaxAttachmentMegabytes") ?? 100, 1, 1024);
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

    public async Task<StudioAttachment> AddAsync(Guid conversationId, IFormFile file, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using Stream source = file.OpenReadStream();
        return await AddStreamAsync(conversationId, file.FileName, file.ContentType, source, visibility, uploadedBy, cancellationToken);
    }

    public async Task<StudioAttachment> AddBytesAsync(Guid conversationId, string fileName, string contentType, byte[] bytes, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        await using MemoryStream source = new(bytes, writable: false);
        return await AddStreamAsync(conversationId, fileName, contentType, source, visibility, uploadedBy, cancellationToken);
    }

    public async Task<StudioAttachment> AddStreamAsync(Guid conversationId, string fileName, string contentType, Stream source, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        string originalName = NormalizeFileName(fileName);
        string normalizedVisibility = string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase) ? "Internal" : "External";
        Guid id = Guid.NewGuid();
        string storageKey = BuildStoredName(id, originalName);

        StoredFile stored = await storage.StoreAsync(
            new StorageWriteRequest(
                storageKey,
                source,
                contentType,
                originalName,
                uploadedBy,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["application"] = "Studio",
                    ["conversationId"] = conversationId.ToString("D"),
                    ["visibility"] = normalizedVisibility
                },
                maximumBytes),
            cancellationToken);

        StudioAttachment attachment = new(
            id,
            conversationId,
            originalName,
            stored.StorageKey,
            stored.ContentType,
            stored.Length,
            normalizedVisibility,
            stored.StoredUtc,
            stored.Sha256,
            stored.UploadedBy,
            stored.Status.ToString());

        try
        {
            InsertAttachment(attachment);
            return attachment;
        }
        catch
        {
            await storage.DeleteAsync(stored.StorageKey, cancellationToken);
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

    public Task<Stream> OpenReadAsync(StudioAttachment attachment, CancellationToken cancellationToken = default)
        => storage.OpenReadAsync(attachment.StoredName, cancellationToken);

    public Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
        => storage.CheckHealthAsync(cancellationToken);

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

    private void InsertAttachment(StudioAttachment attachment)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_attachments(id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)";
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
        command.ExecuteNonQuery();
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

public sealed record StudioAttachment(Guid Id, Guid ConversationId, string FileName, string StoredName, string ContentType, long Size, string Visibility, DateTimeOffset CreatedAt, string Sha256, string UploadedBy, string StorageStatus);
