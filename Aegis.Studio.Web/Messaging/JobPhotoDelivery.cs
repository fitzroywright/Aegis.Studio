using System.Security.Claims;
using System.Security.Cryptography;
using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed record JobPhotoSendRequest(Guid RequestId, Guid[] VersionIds, string Subject, string Body);

public static class JobPhotoDelivery
{
    public const int MaximumBytes = 2_500_000;
    public static void ValidateSelection(Guid[]? ids)
    {
        if(ids is null || ids.Length is < 1 or > 10 || ids.Any(id=>id==Guid.Empty) || ids.Distinct().Count()!=ids.Length)
            throw new ArgumentException("Select one to ten distinct photographs.");
    }

    public static async Task<UnifiedSendResult> SendAsync(Guid jobId, JobPhotoSendRequest request, ClaimsPrincipal user,
        StudioDatabase db, JobAccessService access, StudioAddressBook book, StudioUnifiedMessaging router,
        StudioInAppMessageStore messages, IFileStorage storage, IWebHostEnvironment environment, CancellationToken token)
    {
        if(!access.HasPermission(user,jobId,"ManageProduction") || !access.HasPermission(user,jobId,"ViewAttachments")
            || !access.HasPermission(user,jobId,"ReplyRequester")) throw new UnauthorizedAccessException();
        ValidateSelection(request.VersionIds);
        if(request.RequestId==Guid.Empty || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length>200
            || request.Subject.Contains('\r') || request.Subject.Contains('\n') || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length>10000)
            throw new ArgumentException("Supply a subject and message.");
        Guid clientId; string email;
        using(var c=db.OpenConnection())
        {
            using(var q=new NpgsqlCommand("select c.id,coalesce(c.email,'') from studio_jobs j join studio_clients c on c.id=j.client_id where j.id=$1",c))
            {
                q.Parameters.AddWithValue(jobId);
                using var r=q.ExecuteReader();
                if(!r.Read())throw new ArgumentException("Link the Job to a client before sending photographs.");
                clientId=r.GetGuid(0);email=StudioAddressBook.ValidateAddress(r.GetString(1));
            }
            FinanceReleaseStore.Require(c,null,jobId);
            foreach(var id in request.VersionIds)
            {
                using var q=new NpgsqlCommand("""
                    select exists(select 1 from studio_media_assets a join studio_asset_versions v
                      on v.id=a.approved_version_id and v.job_id=a.job_id and v.asset_key=a.asset_key
                      where a.job_id=$1 and v.id=$2 and a.deleted_at_utc is null
                      and a.production_status not in ('Blocked','Needs Retake','Needs QC')
                      and not exists(select 1 from studio_production_items i where i.asset_id=a.id and i.source_version_id=v.id and i.qc_state not in ('Passed','Cancelled')))
                    """,c);
                q.Parameters.AddWithValue(jobId);q.Parameters.AddWithValue(id);
                if(q.ExecuteScalar() is not true)throw new ArgumentException("Only approved, active photographs with completed quality checks may be sent.");
            }
        }
        var destination=book.Get(clientId,user);
        if(destination.ContactType!="Client" || destination.DeliveryMode!="ExternalOnly" || destination.Status!="Active"
            || StudioAddressBook.Normalize(destination.EmailAddress)!=email)
            throw new ArgumentException("The client email routing does not match the Job's client.");
        var thread=messages.GetOrCreateScoped(user,"Job",jobId);
        router.AssertSendAccess(thread.Id,user);
        var files=new FormFileCollection();
        var buffers=new List<MemoryStream>();
        try
        {
            long total=0;
            var versions=new StudioVersionStore(db,storage);
            foreach(var id in request.VersionIds.Order())
            {
                var version=versions.Get(jobId,id)??throw new KeyNotFoundException();
                if(!string.Equals(version.ContentType,"image/jpeg",StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Only approved JPEG photographs can be emailed.");
                if(version.SizeBytes<=0 || version.SizeBytes>MaximumBytes-total)
                    throw new ArgumentException("Selected photographs exceed the 2.5 MB email attachment limit.");
                await using var source=await new StoredVersionRecovery(storage,environment).OpenAsync(version,token);
                var buffer=new MemoryStream();buffers.Add(buffer);
                byte[] chunk=new byte[65536];int read;
                while((read=await source.ReadAsync(chunk.AsMemory(),token))!=0)
                {
                    if(read>MaximumBytes-total-buffer.Length)throw new ArgumentException("Selected photographs exceed the email attachment limit.");
                    await buffer.WriteAsync(chunk.AsMemory(0,read),token);
                }
                if(buffer.Length!=version.SizeBytes || !Convert.ToHexString(SHA256.HashData(buffer.ToArray())).Equals(version.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stored photograph integrity check failed.");
                total+=buffer.Length;buffer.Position=0;
                files.Add(new FormFile(buffer,0,buffer.Length,"file",id.ToString("N")+"-"+Path.GetFileName(version.FileName))
                    {Headers=new HeaderDictionary(),ContentType="image/jpeg"});
            }
            return await router.SendAsync(new UnifiedSendRequest(request.RequestId,thread.Id,request.Subject,request.Body,[clientId]),files,user,token);
        }
        finally { foreach(var buffer in buffers)buffer.Dispose(); }
    }
}
