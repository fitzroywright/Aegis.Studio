using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed record PhotoLinkAccess(string Token, Guid? VersionId);
public static class JobPhotoLinks
{
    public static void EnsureSchema(StudioDatabase db)
    {
        using var c=db.OpenConnection();
        using var q=new NpgsqlCommand("""
            create table if not exists studio_job_photo_links(
              id uuid primary key, job_id uuid not null references studio_jobs(id) on delete cascade,
              client_id uuid not null references studio_clients(id), actor text not null,
              token_hash text not null unique, versions uuid[] not null, expires_at timestamptz not null,
              revoked_at timestamptz null, created_at timestamptz not null default now(),
              download_count bigint not null default 0, last_download_at timestamptz null);
            """,c);q.ExecuteNonQuery();
    }

    static string Hash(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    static bool Enabled(IConfiguration config)=>config.GetValue<bool>("PhotoDelivery:LinksEnabled");
    public static string Create(StudioDatabase db,IConfiguration config,Guid job,Guid client,Guid requestId,string actor,Guid[] versions)
    {
        if(!Enabled(config))throw new ArgumentException("Individual download links are disabled.");
        string key=config["PhotoDelivery:LinkSigningKey"]??"";
        if(Encoding.UTF8.GetByteCount(key)<32)throw new ArgumentException("Configure a private photo link signing key.");
        if(!Uri.TryCreate(config["PhotoDelivery:DownloadOrigin"],UriKind.Absolute,out var origin)||origin.Scheme!="https"
            ||origin.AbsolutePath!="/"||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!="")
            throw new ArgumentException("Configure PhotoDelivery:DownloadOrigin as an HTTPS Studio origin.");
        string token=Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),Encoding.UTF8.GetBytes($"studio-photo-link-v1:{job:D}:{client:D}:{requestId:D}:{actor}")));
        string hash=Hash(token);versions=versions.Order().ToArray();
        int days=Math.Clamp(config.GetValue("PhotoDelivery:LinkExpiryDays",7),1,30);
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();
        using(var insert=new NpgsqlCommand("""
            insert into studio_job_photo_links(id,job_id,client_id,actor,token_hash,versions,expires_at)
            values($1,$2,$3,$4,$5,$6,now()+make_interval(days=>$7))
            on conflict(id) do nothing
            """,c,tx))
        {
            StudioUnifiedMessaging.Add(insert,requestId,job,client,actor,hash,versions,days);insert.ExecuteNonQuery();
        }
        using(var check=new NpgsqlCommand("""
            select exists(select 1 from studio_job_photo_links where id=$1 and job_id=$2 and client_id=$3
              and actor=$4 and token_hash=$5 and versions=$6 and revoked_at is null and expires_at>now())
            """,c,tx))
        {
            StudioUnifiedMessaging.Add(check,requestId,job,client,actor,hash,versions);
            if(check.ExecuteScalar() is not true)throw new ArgumentException("This link request has changed, expired or been revoked. Start a new delivery.");
        }
        tx.Commit();
        return origin.GetLeftPart(UriPartial.Authority)+"/photo-download.html#"+token;
    }

    public static void Map(WebApplication app)
    {
        EnsureSchema(app.Services.GetRequiredService<StudioDatabase>());
        app.MapPost("/api/photo-download",async(PhotoLinkAccess input,HttpContext context,StudioDatabase db,
            IConfiguration config,IFileStorage storage,IWebHostEnvironment environment,CancellationToken ct)=>
        {
            context.Response.Headers.CacheControl="no-store";
            context.Response.Headers["Referrer-Policy"]="no-referrer";
            context.Response.Headers["X-Content-Type-Options"]="nosniff";
            if(!Enabled(config)||input.Token is null||input.Token.Length!=64||!input.Token.All(Uri.IsHexDigit))return Results.NotFound();
            Guid link,job;Guid[] versions;
            using(var c=db.OpenConnection())
            {
                using(var q=new NpgsqlCommand("""
                    select l.id,l.job_id,l.versions from studio_job_photo_links l join studio_jobs j on j.id=l.job_id and j.client_id=l.client_id
                    where l.token_hash=$1 and l.revoked_at is null and l.expires_at>now() and j.status<>'Cancelled'
                    """,c))
                {
                    q.Parameters.AddWithValue(Hash(input.Token));
                    using var r=q.ExecuteReader();if(!r.Read())return Results.NotFound();
                    link=r.GetGuid(0);job=r.GetGuid(1);versions=r.GetFieldValue<Guid[]>(2);
                }
                if(!FinanceReleaseStore.Decision(c,null,job).Authorized)return Results.NotFound();
                foreach(var version in versions)
                {
                    using var q=new NpgsqlCommand("""
                        select exists(select 1 from studio_media_assets a join studio_asset_versions v on v.id=a.approved_version_id
                         and v.job_id=a.job_id and v.asset_key=a.asset_key where a.job_id=$1 and v.id=$2
                         and a.deleted_at_utc is null and v.content_type='image/jpeg'
                         and a.production_status not in ('Blocked','Needs Retake','Needs QC')
                         and not exists(select 1 from studio_production_items i where i.asset_id=a.id and i.source_version_id=v.id and i.qc_state not in ('Passed','Cancelled')))
                        """,c);
                    StudioUnifiedMessaging.Add(q,job,version);if(q.ExecuteScalar() is not true)return Results.NotFound();
                }
            }
            var store=new StudioVersionStore(db,storage);
            if(input.VersionId is null)
                return Results.Ok(versions.Select(id=>store.Get(job,id)).Where(v=>v is not null).Select(v=>new{versionId=v!.Id,fileName=v.FileName}).ToArray());
            if(!versions.Contains(input.VersionId.Value))return Results.NotFound();
            var photo=store.Get(job,input.VersionId.Value);
            if(photo is null||photo.SizeBytes<1||photo.SizeBytes>100_000_000)return Results.NotFound();
            await using var source=await new StoredVersionRecovery(storage,environment).OpenAsync(photo,ct);
            using var buffer=new MemoryStream();byte[] chunk=new byte[65536];int n;
            while((n=await source.ReadAsync(chunk.AsMemory(),ct))!=0)
            {
                if(buffer.Length+n>photo.SizeBytes)return Results.NotFound();
                await buffer.WriteAsync(chunk.AsMemory(0,n),ct);
            }
            byte[] bytes=buffer.ToArray();
            if(bytes.LongLength!=photo.SizeBytes||!Convert.ToHexString(SHA256.HashData(bytes)).Equals(photo.Sha256,StringComparison.OrdinalIgnoreCase))return Results.NotFound();
            using(var c=db.OpenConnection())
            using(var q=new NpgsqlCommand("update studio_job_photo_links set download_count=download_count+1,last_download_at=now() where id=$1 and revoked_at is null and expires_at>now()",c))
            {
                q.Parameters.AddWithValue(link);if(q.ExecuteNonQuery()!=1)return Results.NotFound();
            }
            return Results.File(bytes,"image/jpeg",Path.GetFileName(photo.FileName));
        }).AllowAnonymous();
        var group=app.MapGroup("/api/messages/jobs/{job:guid}/photo-links").RequireAuthorization();
        group.MapGet("",(Guid job,ClaimsPrincipal user,StudioDatabase db,JobAccessService access)=>
        {
            if(!access.HasPermission(user,job,"ManageProduction")||!access.HasPermission(user,job,"ReplyRequester"))return Results.Forbid();
            using var c=db.OpenConnection();using var q=new NpgsqlCommand("select id,expires_at,revoked_at,download_count,last_download_at from studio_job_photo_links where job_id=$1 order by created_at desc",c);
            q.Parameters.AddWithValue(job);using var r=q.ExecuteReader();var items=new List<object>();
            while(r.Read())items.Add(new{id=r.GetGuid(0),expiresAt=r.GetFieldValue<DateTimeOffset>(1),revokedAt=r.IsDBNull(2)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(2),downloadCount=r.GetInt64(3),lastDownloadAt=r.IsDBNull(4)?(DateTimeOffset?)null:r.GetFieldValue<DateTimeOffset>(4)});
            return Results.Ok(items);
        });
        group.MapPost("/{id:guid}/revoke",(Guid job,Guid id,ClaimsPrincipal user,StudioDatabase db,JobAccessService access)=>
        {
            if(!access.HasPermission(user,job,"ManageProduction")||!access.HasPermission(user,job,"ReplyRequester"))return Results.Forbid();
            using var c=db.OpenConnection();using var q=new NpgsqlCommand("update studio_job_photo_links set revoked_at=coalesce(revoked_at,now()) where id=$1 and job_id=$2",c);
            StudioUnifiedMessaging.Add(q,id,job);return q.ExecuteNonQuery()==1?Results.NoContent():Results.NotFound();
        });
    }
}
