using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Storage;

public sealed class AttachmentStore
{
    private readonly StudioDatabase database;
    private readonly string attachmentDirectory;

    public AttachmentStore(IWebHostEnvironment environment, IConfiguration configuration, StudioDatabase database)
    {
        this.database = database;
        string configuredPath = configuration["Storage:AttachmentsPath"] ?? string.Empty;
        attachmentDirectory = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, "Storage", "attachments")
            : Path.GetFullPath(configuredPath);
        Directory.CreateDirectory(attachmentDirectory);
    }

    public IReadOnlyList<StudioAttachment> GetForConversation(Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc from studio_attachments where conversation_id=$1 order by created_at_utc desc";
        command.Parameters.AddWithValue(conversationId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioAttachment> values=[];
        while(reader.Read()) values.Add(Read(reader));
        return values;
    }

    public async Task<StudioAttachment> AddAsync(Guid conversationId, IFormFile file, string visibility, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        string originalName = Path.GetFileName(file.FileName);
        string extension = Path.GetExtension(originalName);
        Guid id = Guid.NewGuid();
        string storedName = id.ToString("N") + extension;
        string fullPath = Path.Combine(attachmentDirectory, storedName);
        await using (FileStream stream = File.Create(fullPath)) await file.CopyToAsync(stream, cancellationToken);
        StudioAttachment attachment = new(id,conversationId,originalName,storedName,string.IsNullOrWhiteSpace(file.ContentType)?"application/octet-stream":file.ContentType,file.Length,string.Equals(visibility,"Internal",StringComparison.OrdinalIgnoreCase)?"Internal":"External",DateTimeOffset.UtcNow);
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="insert into studio_attachments(id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc) values ($1,$2,$3,$4,$5,$6,$7,$8)";command.Parameters.AddWithValue(attachment.Id);command.Parameters.AddWithValue(attachment.ConversationId);command.Parameters.AddWithValue(attachment.FileName);command.Parameters.AddWithValue(attachment.StoredName);command.Parameters.AddWithValue(attachment.ContentType);command.Parameters.AddWithValue(attachment.Size);command.Parameters.AddWithValue(attachment.Visibility);command.Parameters.AddWithValue(attachment.CreatedAt);command.ExecuteNonQuery();
        return attachment;
    }

    public StudioAttachment? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select id,conversation_id,file_name,stored_name,content_type,size_bytes,visibility,created_at_utc from studio_attachments where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    public string GetPhysicalPath(StudioAttachment attachment) => Path.Combine(attachmentDirectory, attachment.StoredName);
    private static StudioAttachment Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetFieldValue<DateTimeOffset>(7));
}

public sealed record StudioAttachment(Guid Id,Guid ConversationId,string FileName,string StoredName,string ContentType,long Size,string Visibility,DateTimeOffset CreatedAt);
