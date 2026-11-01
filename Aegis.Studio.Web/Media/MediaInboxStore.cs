using System.Security.Cryptography;
using Aegis.Studio.Web.Persistence;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Media;

public sealed record MediaInboxMatch(Guid JobId,Guid? SessionId,string? Collection=null,Guid? SubjectId=null);

public sealed record MediaInboxItem(
    Guid Id, Guid BatchId, string FileName, string ContentType, long SizeBytes,
    string Sha256, string UploadedBy, DateTimeOffset UploadedAtUtc,
    Guid? MatchedJobId=null, Guid? MatchedVersionId=null);

public sealed class MediaInboxStore(StudioDatabase database, IFileStorage storage)
{
    public const string Schema="""
        create table if not exists studio_media_inbox(
          id uuid primary key,
          batch_id uuid not null unique,
          file_name text not null,
          content_type text not null,
          size_bytes bigint not null,
          sha256 text not null,
          storage_key text not null,
          uploaded_by text not null,
          uploaded_at_utc timestamptz not null default now(),
          matched_job_id uuid null references studio_jobs(id) on delete set null,
          matched_version_id uuid null,
          matched_at_utc timestamptz null,
          deleted_at_utc timestamptz null,
          deleted_by text null
        );
        create index if not exists ix_studio_media_inbox_owner on studio_media_inbox(lower(uploaded_by),uploaded_at_utc desc) where deleted_at_utc is null;
        create index if not exists ix_studio_media_inbox_unmatched on studio_media_inbox(uploaded_at_utc desc) where deleted_at_utc is null and matched_job_id is null;
        """;

    public async Task<MediaInboxItem> StoreAsync(Guid id,IFormFile file,string actor,CancellationToken cancellationToken)
    {
        if(id==Guid.Empty)throw new ArgumentException("A stable upload ID is required.");
        if(file.Length<=0||file.Length>100_000_000)throw new ArgumentException("Files must be between 1 byte and 100 MB.");
        string name=Path.GetFileName(file.FileName);
        if(string.IsNullOrWhiteSpace(name)||name.Length>255)throw new ArgumentException("Choose a file with a valid name of at most 255 characters.");
        string contentType=string.IsNullOrWhiteSpace(file.ContentType)?"application/octet-stream":file.ContentType;
        string hash;
        using(var source=file.OpenReadStream())hash=Convert.ToHexString(SHA256.HashData(source));
        string key=$"MediaInbox/{id:N}/original";
        Guid batch=Guid.NewGuid();
        using var connection=database.OpenConnection();
        using var transaction=connection.BeginTransaction();
        using(var guard=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",connection,transaction))
        {guard.Parameters.AddWithValue("studio-media-inbox:"+id.ToString("D"));guard.ExecuteNonQuery();}
        using(var prior=new NpgsqlCommand("select id,batch_id,file_name,content_type,size_bytes,sha256,storage_key,uploaded_by,uploaded_at_utc,matched_job_id,matched_version_id from studio_media_inbox where id=$1",connection,transaction))
        {
            prior.Parameters.AddWithValue(id);using var reader=prior.ExecuteReader();
            if(reader.Read())
            {
                var item=Read(reader,id);
                if(!item.UploadedBy.Equals(actor,StringComparison.OrdinalIgnoreCase)||item.FileName!=name||item.SizeBytes!=file.Length||!item.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("This upload ID was already used for different file bytes or another uploader.");
                return item;
            }
        }
        bool stored=false,committed=false;
        try
        {
            using(var source=file.OpenReadStream())
            {
                var result=await storage.StoreAsync(new StorageWriteRequest(key,source,contentType,name,actor,
                    new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                    {["Application"]="Aegis.Studio",["Area"]="MediaInbox",["UploadId"]=id.ToString("D")}),cancellationToken);
                stored=true;
                if(result.Length!=file.Length||!result.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stored inbox upload failed its size or checksum verification.");
                using var verified=await storage.OpenReadAsync(key,cancellationToken);
                string storedHash=Convert.ToHexString(SHA256.HashData(verified));
                if(!storedHash.Equals(hash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stored inbox upload failed its read-back checksum verification.");
            }
            using(var insert=new NpgsqlCommand("""
                insert into studio_media_inbox(id,batch_id,file_name,content_type,size_bytes,sha256,storage_key,uploaded_by)
                values($1,$2,$3,$4,$5,$6,$7,$8)
                """,connection,transaction))
            {
                insert.Parameters.AddWithValue(id);insert.Parameters.AddWithValue(batch);insert.Parameters.AddWithValue(name);
                insert.Parameters.AddWithValue(contentType);insert.Parameters.AddWithValue(file.Length);insert.Parameters.AddWithValue(hash);
                insert.Parameters.AddWithValue(key);insert.Parameters.AddWithValue(actor);insert.ExecuteNonQuery();
            }
            transaction.Commit();committed=true;
            return new(id,batch,name,contentType,file.Length,hash,actor,DateTimeOffset.UtcNow);
        }
        catch
        {
            if(stored&&!committed)try{await storage.DeleteAsync(key,cancellationToken);}catch{}
            throw;
        }
    }

    public IReadOnlyList<MediaInboxItem> List(string actor,bool canReviewAll)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("""
            select id,batch_id,file_name,content_type,size_bytes,sha256,uploaded_by,uploaded_at_utc,matched_job_id,matched_version_id
            from studio_media_inbox
            where deleted_at_utc is null and ($1 or lower(uploaded_by)=lower($2))
            order by matched_job_id nulls first,uploaded_at_utc desc limit 500
            """,connection);
        command.Parameters.AddWithValue(canReviewAll);command.Parameters.AddWithValue(actor);
        using var reader=command.ExecuteReader();var rows=new List<MediaInboxItem>();
        while(reader.Read())rows.Add(Read(reader,reader.GetGuid(0)));
        return rows;
    }

    public MediaInboxItem? Get(Guid id,string actor,bool canReviewAll)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("""
            select id,batch_id,file_name,content_type,size_bytes,sha256,uploaded_by,uploaded_at_utc,matched_job_id,matched_version_id
            from studio_media_inbox where id=$1 and deleted_at_utc is null and ($2 or lower(uploaded_by)=lower($3))
            """,connection);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(canReviewAll);command.Parameters.AddWithValue(actor);
        using var reader=command.ExecuteReader();return reader.Read()?Read(reader,id):null;
    }

    public async Task<Stream> OpenReadAsync(Guid id,string actor,bool canReviewAll,CancellationToken cancellationToken)
    {
        string key=GetStorageKey(id,actor,canReviewAll)??throw new KeyNotFoundException("Media Inbox item was not found.");
        return await storage.OpenReadAsync(key,cancellationToken);
    }

    private string? GetStorageKey(Guid id,string actor,bool canReviewAll)
    {
        using var connection=database.OpenConnection();
        using var command=new NpgsqlCommand("select storage_key from studio_media_inbox where id=$1 and deleted_at_utc is null and ($2 or lower(uploaded_by)=lower($3))",connection);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(canReviewAll);command.Parameters.AddWithValue(actor);
        return command.ExecuteScalar() as string;
    }

    public void MarkMatched(Guid id,Guid jobId,Guid versionId,string actor,bool canReviewAll)
    {
        using var connection=database.OpenConnection();using var transaction=connection.BeginTransaction();
        using var command=new NpgsqlCommand("""
            update studio_media_inbox set matched_job_id=$2,matched_version_id=$3,matched_at_utc=now()
            where id=$1 and deleted_at_utc is null and ($4 or lower(uploaded_by)=lower($5))
              and (matched_job_id is null or (matched_job_id=$2 and matched_version_id=$3))
            """,connection,transaction);
        command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(jobId);command.Parameters.AddWithValue(versionId);
        command.Parameters.AddWithValue(canReviewAll);command.Parameters.AddWithValue(actor);
        if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("This inbox item was matched elsewhere. Refresh the Media Inbox.");
        transaction.Commit();
    }

    public async Task DeleteAsync(Guid id,string actor,bool canReviewAll,CancellationToken cancellationToken)
    {
        string? key=GetStorageKey(id,actor,canReviewAll);
        if(key is null)throw new KeyNotFoundException("Media Inbox item was not found.");
        using(var connection=database.OpenConnection())
        using(var command=new NpgsqlCommand("""
            update studio_media_inbox set deleted_at_utc=now(),deleted_by=$2
            where id=$1 and deleted_at_utc is null and matched_job_id is null and ($3 or lower(uploaded_by)=lower($2))
            """,connection))
        {
            command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(actor);command.Parameters.AddWithValue(canReviewAll);
            if(command.ExecuteNonQuery()!=1)throw new InvalidOperationException("Matched media cannot be removed from the inbox.");
        }
        await storage.DeleteAsync(key,cancellationToken);
    }

    private static MediaInboxItem Read(NpgsqlDataReader reader,Guid id)=>new(
        id,reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),
        reader.GetString(5),reader.GetString(6),reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8)?null:reader.GetGuid(8),reader.IsDBNull(9)?null:reader.GetGuid(9));
}
