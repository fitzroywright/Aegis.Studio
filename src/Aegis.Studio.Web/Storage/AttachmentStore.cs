using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
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
        List<StudioAttachment> values=[];
        while(reader.Read()) values.Add(Read(reader));
        return values;
    }

    public async Task<StudioAttachment> AddAsync(Guid conversationId, IFormFile file, string visibility, string uploadedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length <= 0 || file.Length > maximumBytes) throw new InvalidOperationException($"Attachment must be between 1 byte and {maximumBytes / 1024L / 1024L} MB.");
        string originalName = Path.GetFileName(file.FileName).Trim();
        if (string.IsNullOrWhiteSpace(originalName)) throw new InvalidOperationException("Attachment file name is required.");
        string extension = Path.GetExtension(originalName);
        Guid id = Guid.NewGuid();
        string storedName = id.ToString("N") + extension.ToLowerInvariant();
        string finalPath = Path.Combine(attachmentDirectory, storedName);
        string temporaryPath = finalPath + ".uploading";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.CopyToAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            await using FileStream hashStream = File.OpenRead(temporaryPath);
            byte[] hash = await SHA256.HashDataAsync(hashStream, cancellationToken);
            string sha256 = Convert.ToHexString(hash).ToLowerInvariant();
            File.Move(temporaryPath, finalPath, false);

            StudioAttachment attachment = new(id,conversationId,originalName,storedName,string.IsNullOrWhiteSpace(file.ContentType)?"application/octet-stream":file.ContentType.Trim(),file.Length,string.Equals(visibility,"Internal",StringComparison.OrdinalIgnoreCase)?"Internal":"External",DateTimeOffset.UtcNow,sha256,string.IsNullOrWhiteSpace(uploadedBy)?"Unknown":uploadedBy.Trim(),"Stored");
            using NpgsqlConnection connection=database.OpenConnection();
            using NpgsqlCommand command=connection.CreateCommand();
            command.CommandText="insert into studio_attachments(id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)";
            command.Parameters.AddWithValue(attachment.Id);command.Parameters.AddWithValue(attachment.ConversationId);command.Parameters.AddWithValue(attachment.FileName);command.Parameters.AddWithValue(attachment.StoredName);command.Parameters.AddWithValue(attachment.ContentType);command.Parameters.AddWithValue(attachment.Size);command.Parameters.AddWithValue(attachment.Visibility);command.Parameters.AddWithValue(attachment.CreatedAt);command.Parameters.AddWithValue(attachment.Sha256);command.Parameters.AddWithValue(attachment.UploadedBy);command.Parameters.AddWithValue(attachment.StorageStatus);command.ExecuteNonQuery();
            return attachment;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            throw;
        }
    }

    public StudioAttachment? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    public string GetPhysicalPath(StudioAttachment attachment)
    {
        string path = Path.GetFullPath(Path.Combine(attachmentDirectory, attachment.StoredName));
        string root = Path.GetFullPath(attachmentDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Attachment path escaped the configured storage root.");
        return path;
    }

    private const string SelectSql="select id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc,sha256,uploaded_by,storage_status from studio_attachments";
    private static StudioAttachment Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7),r.IsDBNull(8)?string.Empty:r.GetString(8),r.IsDBNull(9)?"Unknown":r.GetString(9),r.IsDBNull(10)?"Stored":r.GetString(10));
}

public sealed record StudioAttachment(Guid Id,Guid ConversationId,string FileName,string StoredName,string ContentType,long Size,string Visibility,DateTimeOffset CreatedAt,string Sha256,string UploadedBy,string StorageStatus);
