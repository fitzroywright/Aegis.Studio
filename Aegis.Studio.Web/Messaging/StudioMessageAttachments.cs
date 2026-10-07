using System.Security.Claims;
using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed class StudioMessageAttachmentStore
{
    private readonly StudioDatabase database;
    private readonly IFileStorage storage;
    private readonly string deliveryCacheRoot;
    private readonly long maximumBytes;

    public StudioMessageAttachmentStore(IWebHostEnvironment environment,IConfiguration configuration,StudioDatabase database,IFileStorage storage)
    {
        this.database=database;
        this.storage=new PrefixedFileStorage(
            storage??throw new ArgumentNullException(nameof(storage)),
            "Storage/attachments/messages");
        deliveryCacheRoot=Path.Combine(environment.ContentRootPath,"Storage",".message-delivery-cache");
        Directory.CreateDirectory(deliveryCacheRoot);
        maximumBytes=Math.Clamp(configuration.GetValue<int?>("Storage:MaxAttachmentMegabytes")??100,1,1024)*1024L*1024L;
    }

    public void EnsureSchema()
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
            create table if not exists studio_inapp_message_attachments
            (
                id uuid primary key,
                message_id uuid not null references studio_inapp_messages(id) on delete cascade,
                file_name text not null,
                stored_name text not null,
                content_type text not null,
                size_bytes bigint not null,
                sha256 text not null,
                uploaded_by text not null,
                created_at_utc timestamptz not null
            );
            create index if not exists ix_inapp_message_attachments_message on studio_inapp_message_attachments(message_id);
            """;
        q.ExecuteNonQuery();
    }

    public Guid? GetThreadId(Guid messageId)
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="select thread_id from studio_inapp_messages where id=$1";q.Parameters.AddWithValue(messageId);
        object? value=q.ExecuteScalar();return value is Guid id?id:null;
    }

    public async Task<StudioMessageAttachment> AddAsync(Guid messageId,IFormFile file,string uploadedBy,CancellationToken token)
    {
        if(file.Length<=0||file.Length>maximumBytes)throw new InvalidOperationException($"Attachment must be between 1 byte and {maximumBytes/1024L/1024L} MB.");
        string name=Path.GetFileName(file.FileName).Trim();if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Attachment file name is required.");
        Guid id=Guid.NewGuid();string storedName=id.ToString("N")+Path.GetExtension(name).ToLowerInvariant();
        await using Stream source=file.OpenReadStream();
        StoredFile stored=await storage.StoreAsync(new StorageWriteRequest(
            storedName,source,string.IsNullOrWhiteSpace(file.ContentType)?"application/octet-stream":file.ContentType,
            name,uploadedBy,new Dictionary<string,string>{{"Application","Aegis.Studio"},{"MessageId",messageId.ToString("D")}},maximumBytes),token);
        StudioMessageAttachment result=new(id,messageId,name,storedName,string.IsNullOrWhiteSpace(file.ContentType)?"application/octet-stream":file.ContentType,stored.Length,stored.Sha256,uploadedBy,DateTimeOffset.UtcNow);
        try
        {
            using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
            q.CommandText="insert into studio_inapp_message_attachments(id,message_id,file_name,stored_name,content_type,size_bytes,sha256,uploaded_by,created_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9)";
            q.Parameters.AddWithValue(result.Id);q.Parameters.AddWithValue(result.MessageId);q.Parameters.AddWithValue(result.FileName);q.Parameters.AddWithValue(result.StoredName);q.Parameters.AddWithValue(result.ContentType);q.Parameters.AddWithValue(result.Size);q.Parameters.AddWithValue(result.Sha256);q.Parameters.AddWithValue(result.UploadedBy);q.Parameters.AddWithValue(result.CreatedAtUtc);q.ExecuteNonQuery();
            DelayOutbound(messageId,c);
            return result;
        }
        catch
        {
            await storage.DeleteAsync(storedName,token);throw;
        }
    }

    public async Task<StudioMessageAttachment> AddBytesAsync(Guid messageId,string fileName,string contentType,byte[] bytes,string uploadedBy,CancellationToken token)
    {
        if(bytes.Length<=0||bytes.LongLength>maximumBytes)throw new InvalidOperationException($"Attachment must be between 1 byte and {maximumBytes/1024L/1024L} MB.");
        string name=Path.GetFileName(fileName).Trim();if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("Attachment file name is required.");
        string incomingSha=Convert.ToHexString(SHA256.HashData(bytes));
        StudioMessageAttachment? existing=GetForMessage(messageId).FirstOrDefault(x=>x.FileName.Equals(name,StringComparison.OrdinalIgnoreCase)&&x.Sha256.Equals(incomingSha,StringComparison.OrdinalIgnoreCase));
        if(existing is not null)return existing;
        Guid id=Guid.NewGuid();string storedName=id.ToString("N")+Path.GetExtension(name).ToLowerInvariant();
        await using var source=new MemoryStream(bytes,false);
        StoredFile stored=await storage.StoreAsync(new StorageWriteRequest(
            storedName,source,string.IsNullOrWhiteSpace(contentType)?"application/octet-stream":contentType,
            name,uploadedBy,new Dictionary<string,string>{{"Application","Aegis.Studio"},{"MessageId",messageId.ToString("D")}},maximumBytes),token);
        StudioMessageAttachment result=new(id,messageId,name,storedName,string.IsNullOrWhiteSpace(contentType)?"application/octet-stream":contentType,stored.Length,stored.Sha256,uploadedBy,DateTimeOffset.UtcNow);
        try
        {
            using NpgsqlConnection db=database.OpenConnection();using NpgsqlCommand q=db.CreateCommand();
            q.CommandText="insert into studio_inapp_message_attachments(id,message_id,file_name,stored_name,content_type,size_bytes,sha256,uploaded_by,created_at_utc) values($1,$2,$3,$4,$5,$6,$7,$8,$9)";
            q.Parameters.AddWithValue(result.Id);q.Parameters.AddWithValue(result.MessageId);q.Parameters.AddWithValue(result.FileName);q.Parameters.AddWithValue(result.StoredName);q.Parameters.AddWithValue(result.ContentType);q.Parameters.AddWithValue(result.Size);q.Parameters.AddWithValue(result.Sha256);q.Parameters.AddWithValue(result.UploadedBy);q.Parameters.AddWithValue(result.CreatedAtUtc);q.ExecuteNonQuery();
            DelayOutbound(messageId,db);
            return result;
        }
        catch
        {
            await storage.DeleteAsync(storedName,token);throw;
        }
    }

    public IReadOnlyList<StudioMessageAttachment> GetForMessage(Guid messageId)
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="select id,message_id,file_name,stored_name,content_type,size_bytes,sha256,uploaded_by,created_at_utc from studio_inapp_message_attachments where message_id=$1 order by created_at_utc";
        q.Parameters.AddWithValue(messageId);using NpgsqlDataReader r=q.ExecuteReader();List<StudioMessageAttachment> values=[];
        while(r.Read())values.Add(Read(r));return values;
    }

    public byte[] ReadBytes(StudioMessageAttachment attachment)
    {
        using Stream source=storage.OpenReadAsync(attachment.StoredName).GetAwaiter().GetResult();
        using var memory=new MemoryStream();source.CopyTo(memory);return memory.ToArray();
    }

    public IReadOnlyList<StudioMessageAttachment> GetForThread(Guid threadId)
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="""
            select a.id,a.message_id,a.file_name,a.stored_name,a.content_type,a.size_bytes,a.sha256,a.uploaded_by,a.created_at_utc
            from studio_inapp_message_attachments a join studio_inapp_messages m on m.id=a.message_id
            where m.thread_id=$1 order by a.created_at_utc
            """;
        q.Parameters.AddWithValue(threadId);using NpgsqlDataReader r=q.ExecuteReader();List<StudioMessageAttachment> values=[];
        while(r.Read())values.Add(Read(r));return values;
    }

    public StudioMessageAttachment? Get(Guid id)
    {
        using NpgsqlConnection c=database.OpenConnection();using NpgsqlCommand q=c.CreateCommand();
        q.CommandText="select id,message_id,file_name,stored_name,content_type,size_bytes,sha256,uploaded_by,created_at_utc from studio_inapp_message_attachments where id=$1";
        q.Parameters.AddWithValue(id);using NpgsqlDataReader r=q.ExecuteReader();return r.Read()?Read(r):null;
    }

    public string GetPhysicalPath(StudioMessageAttachment attachment)
    {
        string path=Path.Combine(deliveryCacheRoot,attachment.StoredName);if(File.Exists(path))return path;
        using Stream source=storage.OpenReadAsync(attachment.StoredName).GetAwaiter().GetResult();
        using FileStream destination=new(path,FileMode.Create,FileAccess.Write,FileShare.Read);
        source.CopyTo(destination);destination.Flush();return path;
    }

    private static void DelayOutbound(Guid messageId,NpgsqlConnection connection)
    {
        using NpgsqlCommand delay=connection.CreateCommand();
        delay.CommandText="""
            update studio_outbound_email
            set next_attempt_at_utc=greatest(coalesce(next_attempt_at_utc,now()),now()+interval '5 seconds')
            where inapp_message_id=$1 and status in ('Pending','Retry')
            """;
        delay.Parameters.AddWithValue(messageId);
        delay.ExecuteNonQuery();
    }

    private static StudioMessageAttachment Read(NpgsqlDataReader r)=>new(
        r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetFieldValue<DateTimeOffset>(8));
}

public sealed record StudioMessageAttachment(Guid Id,Guid MessageId,string FileName,string StoredName,string ContentType,long Size,string Sha256,string UploadedBy,DateTimeOffset CreatedAtUtc);

public static class StudioMessageAttachmentEndpoints
{
    public static void MapStudioMessageAttachments(this WebApplication app)
    {
        StudioMessageAttachmentStore files=app.Services.GetRequiredService<StudioMessageAttachmentStore>();
        files.EnsureSchema();

        app.MapGet("/api/messages/threads/{threadId:guid}/attachments",(Guid threadId,ClaimsPrincipal user,StudioInAppMessageStore messages)=>{
            try{messages.AssertThreadAccess(threadId,user);return Results.Ok(files.GetForThread(threadId));}
            catch(UnauthorizedAccessException){return Results.Forbid();}catch(KeyNotFoundException){return Results.NotFound();}
        }).RequireAuthorization();

        app.MapPost("/api/messages/{messageId:guid}/attachments",async(Guid messageId,IFormFile file,ClaimsPrincipal user,StudioInAppMessageStore messages,CancellationToken token)=>{
            Guid? threadId=files.GetThreadId(messageId);if(!threadId.HasValue)return Results.NotFound();
            try
            {
                messages.AssertThreadAccess(threadId.Value,user);
                if(messages.GetExternalThread(threadId.Value,user) is not null)
                {
                    long existingBytes=files.GetForMessage(messageId).Sum(x=>x.Size);
                    if(existingBytes+file.Length>2_500_000)
                        return Results.BadRequest(new{error="External email attachments cannot exceed 2.5 MB total through the Microsoft 365 transport."});
                }
                StudioMessageAttachment attachment=await files.AddAsync(messageId,file,user.FindFirstValue("login_name")??user.Identity?.Name??"Studio",token);
                return Results.Created($"/api/message-attachments/{attachment.Id}",attachment);
            }
            catch(UnauthorizedAccessException){return Results.Forbid();}
            catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
        }).DisableAntiforgery().RequireAuthorization();

        app.MapGet("/api/message-attachments/{id:guid}",(Guid id,ClaimsPrincipal user,StudioInAppMessageStore messages)=>{
            StudioMessageAttachment? attachment=files.Get(id);if(attachment is null)return Results.NotFound();
            Guid? threadId=files.GetThreadId(attachment.MessageId);if(!threadId.HasValue)return Results.NotFound();
            try
            {
                messages.AssertThreadAccess(threadId.Value,user);
                string path=files.GetPhysicalPath(attachment);
                return File.Exists(path)?Results.File(path,attachment.ContentType,attachment.FileName,enableRangeProcessing:true):Results.NotFound();
            }
            catch(UnauthorizedAccessException){return Results.Forbid();}
        }).RequireAuthorization();
    }
}
