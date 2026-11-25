using System.Security.Claims;
using System.Security.Cryptography;
using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Galleries;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed record JobPhotoSendRequest(Guid RequestId, Guid[] VersionIds, string Subject, string Body, string Quality = "Full", string Method = "Email");

public sealed record JobPhotoSendResult(Guid MessageId, Guid ThreadId, string Status, string? ShareUrl=null);

public static class JobPhotoDelivery
{
    public static string GalleryUrl(string? origin)
    {
        if(!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme!="https" || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath!="/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Configure PublicSite:WebBaseUrl as the HTTPS client website origin.");
        return uri.GetLeftPart(UriPartial.Authority)+"/#galleries";
    }

    public const int MaximumBytes = 2_500_000;
    public static void ValidateQuality(string quality)
    {
        if(quality is not ("Full" or "Compressed" or "Proof")) throw new ArgumentException("Choose Full, Compressed or Proof.");
    }
    public static void ValidateSelection(Guid[]? ids)
    {
        if(ids is null || ids.Length is < 1 or > 10 || ids.Any(id=>id==Guid.Empty) || ids.Distinct().Count()!=ids.Length)
            throw new ArgumentException("Select one to ten distinct photographs.");
    }

    public static async Task<JobPhotoSendResult> SendAsync(Guid jobId, JobPhotoSendRequest request, ClaimsPrincipal user,
        StudioDatabase db, JobAccessService access, StudioAddressBook book, StudioUnifiedMessaging router,
        StudioInAppMessageStore messages, IFileStorage storage, IWebHostEnvironment environment, IConfiguration configuration, CancellationToken token)
    {
        if(!access.HasPermission(user,jobId,"ManageProduction") || !access.HasPermission(user,jobId,"ViewAttachments")
            || !access.HasPermission(user,jobId,"ReplyRequester")) throw new UnauthorizedAccessException();
        ValidateSelection(request.VersionIds);
        ValidateQuality(request.Quality);
        if(request.Method is not ("Email" or "Gallery" or "WhatsApp" or "Link"))throw new ArgumentException("Choose a delivery method.");
        if(request.RequestId==Guid.Empty || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length>200
            || request.Subject.Contains('\r') || request.Subject.Contains('\n') || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length>10000)
            throw new ArgumentException("Supply a subject and message.");
        Guid clientId; string email; string? galleryUrl=null;
        using(var c=db.OpenConnection())
        {
            using(var q=new NpgsqlCommand("select c.id,coalesce(c.email,'') from studio_jobs j join studio_clients c on c.id=j.client_id where j.id=$1",c))
            {
                q.Parameters.AddWithValue(jobId);
                using var r=q.ExecuteReader();
                if(!r.Read())throw new ArgumentException("Link the Job to a client before sending photographs.");
                clientId=r.GetGuid(0);email=StudioAddressBook.ValidateAddress(r.GetString(1));
            }
            if(request.Method=="Link" || (request.Method=="Email" && request.Quality!="Proof"))FinanceReleaseStore.Require(c,null,jobId);
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
            if(request.Method is "Gallery" or "WhatsApp")
            {
                using var q=new NpgsqlCommand("""
                    select exists(select 1 from studio_gallery_exports e
                    join studio_workflow_publications p on p.id=e.publication_id and p.job_id=e.job_id
                    join studio_workflow_submissions s on s.id=p.submission_id
                    join studio_jobs j on j.id=p.job_id
                    where e.job_id=$1 and e.client_id=$2 and j.client_id=$2
                      and e.state='Published' and s.kind='Finished' and s.status='Approved'
                      and j.status in ('Ready for Delivery','Delivered','Completed')
                      and (select array_agg(distinct (x->>'versionId')::uuid order by (x->>'versionId')::uuid) from jsonb_array_elements(s.manifest) x)=$3)
                    """,c);
                q.Parameters.AddWithValue(jobId);q.Parameters.AddWithValue(clientId);q.Parameters.AddWithValue(request.VersionIds.Order().ToArray());
                if(q.ExecuteScalar() is not true)throw new ArgumentException("Select exactly the photographs in an already published client gallery. Publish through Front Desk first.");
                galleryUrl=GalleryUrl(configuration["PublicSite:WebBaseUrl"]);
            }
        }
        var destination=book.Get(clientId,user);
        if(destination.ContactType!="Client" || destination.DeliveryMode!="ExternalOnly" || destination.Status!="Active"
            || StudioAddressBook.Normalize(destination.EmailAddress)!=email)
            throw new ArgumentException("The client email routing does not match the Job's client.");
        var thread=messages.GetOrCreateScoped(user,"Job",jobId);
        router.AssertSendAccess(thread.Id,user);
        if(request.Method=="Link")
        {
            var store=new StudioVersionStore(db,storage);
            foreach(var id in request.VersionIds)
            {
                var photo=store.Get(jobId,id)??throw new KeyNotFoundException();
                if(photo.ContentType!="image/jpeg"||photo.SizeBytes<1||photo.SizeBytes>100_000_000)throw new ArgumentException("Links support approved JPEG photographs up to 100 MB each.");
            }
            string url=JobPhotoLinks.Create(db,configuration,jobId,clientId,request.RequestId,StudioUnifiedMessaging.CurrentUser(user),request.VersionIds);
            string body=request.Body.Trim()+"\n\nDownload your selected photographs using this private, expiring link. Do not forward it: "+url;
            var sent=await router.SendAsync(new UnifiedSendRequest(request.RequestId,thread.Id,request.Subject,body,[clientId]),new FormFileCollection(),user,token);
            return new(sent.MessageId,sent.ThreadId,sent.Status);
        }
        if(galleryUrl is not null)
        {
            string body=request.Body.Trim()+"\n\nOpen your private gallery and sign in with your verified client account: "+galleryUrl;
            if(request.Method=="WhatsApp")
            {
                var audit=messages.AddMessage(thread.Id,user,"WhatsApp share prepared (not sent or delivered by Studio).\n"+request.Subject+"\n"+body,request.RequestId);
                return new(audit.Id,thread.Id,"SharePrepared","https://wa.me/?text="+Uri.EscapeDataString(request.Subject+"\n"+body));
            }
            var sent=await router.SendAsync(new UnifiedSendRequest(request.RequestId,thread.Id,request.Subject,body,[clientId]),new FormFileCollection(),user,token);
            return new(sent.MessageId,sent.ThreadId,sent.Status);
        }
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
                long sourceLimit=request.Quality=="Full"?MaximumBytes-total:100_000_000;
                if(version.SizeBytes<=0 || version.SizeBytes>sourceLimit)
                    throw new ArgumentException("Selected photographs exceed the 2.5 MB email attachment limit.");
                await using var source=await new StoredVersionRecovery(storage,environment).OpenAsync(version,token);
                var buffer=new MemoryStream();buffers.Add(buffer);
                byte[] chunk=new byte[65536];int read;
                while((read=await source.ReadAsync(chunk.AsMemory(),token))!=0)
                {
                    if(read>sourceLimit-buffer.Length)throw new ArgumentException("Selected photographs exceed the email attachment limit.");
                    await buffer.WriteAsync(chunk.AsMemory(0,read),token);
                }
                if(buffer.Length!=version.SizeBytes || !Convert.ToHexString(SHA256.HashData(buffer.ToArray())).Equals(version.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stored photograph integrity check failed.");
                buffer.Position=0;
                if(request.Quality!="Full")
                {
                    byte[] encoded;
                    if(request.Quality=="Proof")encoded=GalleryProofs.Render(buffer);
                    else
                    {
                        using var image=StudioImageProcessor.DecodeOriented(buffer);
                        using var resized=StudioImageProcessor.ResizeMax(image,2048);
                        encoded=StudioImageProcessor.EncodeJpeg(resized,82);
                    }
                    buffer.Dispose();
                    buffer=new MemoryStream(encoded,writable:false);buffers.Add(buffer);
                }
                if(buffer.Length>MaximumBytes-total)throw new ArgumentException("Selected photographs exceed the 2.5 MB email attachment limit. Select fewer photographs.");
                total+=buffer.Length;buffer.Position=0;
                files.Add(new FormFile(buffer,0,buffer.Length,"file",request.Quality.ToLowerInvariant()+"-"+id.ToString("N")+"-"+Path.GetFileName(version.FileName))
                    {Headers=new HeaderDictionary(),ContentType="image/jpeg"});
            }
            var sent=await router.SendAsync(new UnifiedSendRequest(request.RequestId,thread.Id,request.Subject,request.Body,[clientId]),files,user,token);
            return new(sent.MessageId,sent.ThreadId,sent.Status);
        }
        finally { foreach(var buffer in buffers)buffer.Dispose(); }
    }
}
