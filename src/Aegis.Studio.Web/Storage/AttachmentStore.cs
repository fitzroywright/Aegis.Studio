using System.Buffers;
using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
    private const int CopyBufferSize = 128 * 1024;

    private readonly StudioDatabase database;
    private readonly string attachmentDirectory;
    private readonly long maximumBytes;

    public AttachmentStore(IWebHostEnvironment environment, IConfiguration configuration, StudioDatabase database)
    {
        this.database = database;
        string configuredPath = configuration["Storage:AttachmentsPath"] ?? string.Empty;
        attachmentDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Storage", "attachments")
            : Path.GetFullPath(configuredPath);
        int maximumMegabytes = Math.Clamp(configuration.GetValue<int?>("Storage:MaxAttachmentMegabytes") ?? 100, 1, 1024);
        maximumBytes = maximumMegabytes * 1024L * 1024L;
        Directory.CreateDirectory(attachmentDirectory);
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
        if (file.Length <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidOperationException($"Attachment must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        }

        string originalName = NormalizeFileName(file.FileName);
        string contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType.Trim();
        Guid id = Guid.NewGuid();
        string storedName = BuildStoredName(id, originalName);
        string finalPath = Path.Combine(attachmentDirectory, storedName);
        string temporaryPath = finalPath + ".uploading";

        try
        {
            await using Stream source = file.OpenReadStream();
            StreamWriteResult writeResult = await WriteStreamAsync(source, temporaryPath, cancellationToken);
            File.Move(temporaryPath, finalPath, false);

            StudioAttachment attachment = CreateAttachment(
                id,
                conversationId,
                originalName,
                storedName,
                contentType,
                writeResult.BytesWritten,
                visibility,
                uploadedBy,
                writeResult.Sha256);

            InsertAttachment(attachment);
            return attachment;
        }
        catch
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(finalPath);
            throw;
        }
    }

    public async Task<StudioAttachment> AddBytesAsync(Guid conversationId, string fileName, string contentType, byte[] bytes, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length <= 0 || bytes.LongLength > maximumBytes)
        {
            throw new InvalidOperationException($"Attachment must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        }

        await using MemoryStream source = new(bytes, writable: false);
        return await AddStreamAsync(conversationId, fileName, contentType, source, visibility, uploadedBy, cancellationToken);
    }

    public async Task<StudioAttachment> AddStreamAsync(Guid conversationId, string fileName, string contentType, Stream source, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new InvalidOperationException("Attachment source stream must be readable.");
        }

        string originalName = NormalizeFileName(fileName);
        string normalizedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
        Guid id = Guid.NewGuid();
        string storedName = BuildStoredName(id, originalName);
        string finalPath = Path.Combine(attachmentDirectory, storedName);
        string temporaryPath = finalPath + ".uploading";

        try
        {
            StreamWriteResult writeResult = await WriteStreamAsync(source, temporaryPath, cancellationToken);
            File.Move(temporaryPath, finalPath, false);

            StudioAttachment attachment = CreateAttachment(
                id,
                conversationId,
                originalName,
                storedName,
                normalizedContentType,
                writeResult.BytesWritten,
                visibility,
                uploadedBy,
                writeResult.Sha256);

            InsertAttachment(attachment);
            return attachment;
        }
        catch
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(finalPath);
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

    public string GetPhysicalPath(StudioAttachment attachment)
    {
        string path = Path.GetFullPath(Path.Combine(attachmentDirectory, attachment.StoredName));
        string root = Path.GetFullPath(attachmentDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Attachment path escaped the configured storage root.");
        }
        return path;
    }

    private async Task<StreamWriteResult> WriteStreamAsync(Stream source, string temporaryPath, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long totalBytes = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
                if (totalBytes > maximumBytes)
                {
                    throw new InvalidOperationException($"Attachment exceeds the configured {maximumBytes / 1024L / 1024L} MB limit.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (totalBytes == 0)
            {
                throw new InvalidOperationException("Attachment cannot be empty.");
            }

            await destination.FlushAsync(cancellationToken);
            string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return new StreamWriteResult(totalBytes, sha256);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
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
            string.Equals(visibility, "Internal", StringComparison.OrdinalIgnoreCase) ? "Internal" : "External",
            DateTimeOffset.UtcNow,
            sha256,
            string.IsNullOrWhiteSpace(uploadedBy) ? "Unknown" : uploadedBy.Trim(),
            "Stored");

    private void InsertAttachment(StudioAttachment attachment)
    {
        try
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
        catch
        {
            DeleteIfExists(Path.Combine(attachmentDirectory, attachment.StoredName));
            throw;
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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

    private sealed record StreamWriteResult(long BytesWritten, string Sha256);
}

public sealed record StudioAttachment(Guid Id,Guid ConversationId,string FileName,string StoredName,string ContentType,long Size,string Visibility,DateTimeOffset CreatedAt,string Sha256,string UploadedBy,string StorageStatus);
