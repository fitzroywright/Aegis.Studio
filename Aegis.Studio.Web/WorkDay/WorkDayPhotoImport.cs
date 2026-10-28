using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.WorkDay;

/// <summary>Shared photo completion for the API exchange and direct WorkDay upload route.
/// Callers must first verify assignment membership and the Job's AddAttachments permission.</summary>
public static class WorkDayPhotoImport
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static async Task<WorkDayUploadResult> ImportAsync(
        StudioDatabase database,IFileStorage storage,AttachmentStore attachments,StudioStore studio,
        StudioJob job,Guid assignmentId,Guid uploadId,IFormFile file,ClaimsPrincipal user,CancellationToken ct)
    {
        if(uploadId==Guid.Empty||assignmentId==Guid.Empty)throw new ArgumentException("A stable upload and assignment identity are required.");
        if(file.Length<1||file.Length>attachments.MaximumBytes)throw new ArgumentException("The file exceeds the configured WorkDay attachment limit.");
        using var completion=new CompletionLock(database,uploadId);
        string actor=user.FindFirstValue("login_name")??user.FindFirstValue(ClaimTypes.NameIdentifier)??user.Identity?.Name??"";
        if(string.IsNullOrWhiteSpace(actor))throw new UnauthorizedAccessException();
        string name=Path.GetFileName(file.FileName).Trim();
        bool photograph;
        string contentType=file.ContentType;
        await using(var source=file.OpenReadStream())
        {
            byte[] header=new byte[12];int count=await source.ReadAtLeastAsync(header,header.Length,false,ct);
            bool jpeg=count>=3&&header[0]==255&&header[1]==216&&header[2]==255&&
                (name.EndsWith(".jpg",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".jpeg",StringComparison.OrdinalIgnoreCase));
            bool png=count>=8&&header.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10})&&name.EndsWith(".png",StringComparison.OrdinalIgnoreCase);
            bool webp=count>=12&&Encoding.ASCII.GetString(header,0,4)=="RIFF"&&Encoding.ASCII.GetString(header,8,4)=="WEBP"&&name.EndsWith(".webp",StringComparison.OrdinalIgnoreCase);
            photograph=jpeg||png||webp;
            if(photograph)contentType=jpeg?"image/jpeg":png?"image/png":"image/webp";
        }
        await using var normalizedSource=file.OpenReadStream();
        file=new FormFile(normalizedSource,0,file.Length,"file",name){Headers=new HeaderDictionary(),ContentType=contentType};
        ct.ThrowIfCancellationRequested();
        string hash;
        await using(var source=file.OpenReadStream())hash=Convert.ToHexString(await SHA256.HashDataAsync(source,ct));
        // Recover earlier attachment-only imports without storing a second attachment.
        StudioAttachment? stored=attachments.GetForConversation(job.ConversationId).FirstOrDefault(a=>
            a.Visibility=="Internal"&&a.FileName==name&&a.Size==file.Length&&
            a.UploadedBy.Equals(actor,StringComparison.OrdinalIgnoreCase)&&a.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase));
        if(stored?.StorageStatus=="Quarantined")throw new InvalidOperationException("This photograph is quarantined and requires staff review.");
        if(stored is not null&&stored.StorageStatus!="Stored")throw new InvalidOperationException("The earlier attachment is unavailable and requires staff review.");
        Guid? versionId=null,assetId=null;
        if(photograph)
        {
            var media=new MediaProductionStore(database,new StudioVersionStore(database,storage));
            var receipts=new MediaUploadReceipts(database,media);
            Guid? session=SessionForAssignment(database,job.Id,assignmentId,uploadId);
            // A one-file batch uses the upload UUID for deterministic retry and normal session/crew gates.
            receipts.Create(job.Id,new UploadBatchDeclaration(uploadId,session,
                [new UploadFileDeclaration(uploadId,name,file.Length)],Note:$"Uploaded via WorkDay assignment {assignmentId:D}."),actor,false);
            JsonElement result=JsonSerializer.SerializeToElement(receipts.Receive(job.Id,uploadId,file,actor),Json);
            if(result.GetProperty("unmatched").GetBoolean())
                throw new InvalidOperationException("The returned photo needs matching in the Job's unmatched media before it can be acknowledged.");
            versionId=result.GetProperty("id").GetGuid();assetId=result.GetProperty("assetId").GetGuid();
        }
        if(stored is null)
        {
            await using var source=file.OpenReadStream();
            stored=await attachments.AddStreamAsync(job.ConversationId,name,file.ContentType,source,"Internal",actor,ct,
                sourceKey:$"workday:{uploadId:D}",correlationId:$"workday:{uploadId:D}");
        }
        if(stored.StorageStatus!="Stored"||stored.ConversationId!=job.ConversationId||stored.Size!=file.Length||
            !stored.UploadedBy.Equals(actor,StringComparison.OrdinalIgnoreCase)||!stored.Sha256.Equals(hash,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The WorkDay upload identity belongs to different stored content.");
        Guid messageId=new(SHA256.HashData(Encoding.UTF8.GetBytes("workday-photo-message:"+uploadId.ToString("D"))).AsSpan(0,16));
        string displayName=name.Replace('\r',' ').Replace('\n',' ');
        string description=photograph?"A photo was uploaded to this Job":"A file was uploaded to this Job";
        string destination=photograph?"View it in the Job photo gallery.":"View it in the Job attachments.";
        studio.AddMessage(job.ConversationId,new AddMessageRequest(
            $"{description}: {displayName}\nUploaded by {actor} via WorkDay. {destination}",true),user,messageId);
        // Acknowledge only after the gallery, attachment and Job message have all been persisted.
        return new WorkDayUploadResult(uploadId,stored.Id,assignmentId,stored.FileName,stored.Size,stored.StorageStatus,
            DateTimeOffset.UtcNow,job.Id,assetId,versionId,messageId);
    }

    private static Guid? SessionForAssignment(StudioDatabase database,Guid jobId,Guid assignmentId,Guid uploadId)
    {
        using var c=database.OpenConnection();
        using(var prior=new NpgsqlCommand("select session_id from studio_media_upload_batches where id=$1 and job_id=$2",c))
        {
            prior.Parameters.AddWithValue(uploadId);prior.Parameters.AddWithValue(jobId);
            object? recorded=prior.ExecuteScalar();
            if(recorded is Guid previous)return previous;
            if(recorded is DBNull)return null;
        }
        using var q=new NpgsqlCommand("""
            select w.id from studio_workflow_sessions w join studio_shoot_sessions s on s.id=w.shoot_id
            where w.job_id=$1 and s.job_id=$1 and s.schedule_entry_id=$2
            """,c);
        q.Parameters.AddWithValue(jobId);q.Parameters.AddWithValue(assignmentId);
        using var reader=q.ExecuteReader();
        if(!reader.Read())return null;
        Guid session=reader.GetGuid(0);
        if(reader.Read())throw new InvalidOperationException("The assignment maps to multiple photography sessions. Review the Job schedule.");
        return session;
    }

    // Serialize the whole completion, not only media ingestion: concurrent retries must
    // not race while recovering a legacy attachment with no source key.
    private sealed class CompletionLock : IDisposable
    {
        private readonly NpgsqlConnection connection;
        private readonly string key;
        public CompletionLock(StudioDatabase database,Guid uploadId)
        {
            key="workday-photo:"+uploadId.ToString("D");connection=database.OpenConnection();
            try{Run("select pg_advisory_lock(hashtextextended($1,0))");}
            catch{connection.Dispose();throw;}
        }
        private void Run(string sql)
        {using var q=new NpgsqlCommand(sql,connection);q.Parameters.AddWithValue(key);q.ExecuteNonQuery();}
        public void Dispose()
        {try{Run("select pg_advisory_unlock(hashtextextended($1,0))");}finally{connection.Dispose();}}
    }
}
