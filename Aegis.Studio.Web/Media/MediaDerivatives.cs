using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Common.Storage;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;

namespace Aegis.Studio.Web.Media;

// A durable, leased queue over existing versions. Storage bytes never live in SQL.
public sealed class MediaDerivativeWorker(StudioDatabase database, IFileStorage storage,
    IWebHostEnvironment environment, ILogger<MediaDerivativeWorker> logger) : BackgroundService
{
    public const string Schema = """
        create table if not exists studio_media_derivatives(
          version_id uuid primary key references studio_asset_versions(id) on delete cascade,
          status text not null default 'Pending', attempts integer not null default 0,
          next_attempt_at timestamptz not null default now(), thumbnail_key text null,
          preview_key text null, last_error text null, updated_at timestamptz not null default now());
        create or replace function studio_enqueue_media_preview() returns trigger language plpgsql as $$
        begin
          if TG_OP='UPDATE' and new.sha256=old.sha256 and new.stored_path=old.stored_path then return new; end if;
          insert into studio_media_derivatives(version_id) values(new.id) on conflict(version_id)
            do update set status='Pending',attempts=0,next_attempt_at=now(),thumbnail_key=null,preview_key=null,last_error=null;
          return new;
        end $$;
        drop trigger if exists studio_version_preview_queue on studio_asset_versions;
        create trigger studio_version_preview_queue after insert or update of sha256,stored_path on studio_asset_versions
          for each row execute function studio_enqueue_media_preview();
        create index if not exists ix_media_derivatives_pending on studio_media_derivatives(next_attempt_at)
          where status in ('Pending','Processing','Failed');
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One decoder per process. Other instances share leases through SKIP LOCKED.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid? id = await Claim(stoppingToken);
                if (id is null) { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); continue; }
                await Generate(id.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Media preview queue unavailable; retrying in background");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
    private Guid? backfillAfter;
    private bool backfillComplete;
    private async Task<Guid?> Claim(CancellationToken token)
    {
        await using var c = database.OpenConnection();
        // Backfill is incremental; it does not scan or decode images on a request.
        if(!backfillComplete)
        {
            await using var backfill=new NpgsqlCommand("""
                with page as (select id from studio_asset_versions where ($1::uuid is null or id>$1) order by id limit 100),
                queued as (insert into studio_media_derivatives(version_id) select id from page on conflict do nothing returning version_id)
                select max(id::text)::uuid from page
                """,c);
            backfill.Parameters.AddWithValue((object?)backfillAfter??DBNull.Value);
            if(await backfill.ExecuteScalarAsync(token) is Guid cursor)backfillAfter=cursor;else backfillComplete=true;
        }
        await using var cmd = new NpgsqlCommand("""
            update studio_media_derivatives d set status='Processing',attempts=attempts+1,
              next_attempt_at=now()+interval '15 minutes',updated_at=now()
            where version_id=(select version_id from studio_media_derivatives
              where status in ('Pending','Processing','Failed') and next_attempt_at<=now()
              order by next_attempt_at,version_id for update skip locked limit 1)
            returning version_id
            """, c);
        return await cmd.ExecuteScalarAsync(token) is Guid id ? id : null;
    }
    private async Task Generate(Guid id, CancellationToken token)
    {
        try
        {
            await using var c = database.OpenConnection();
            await using var find = new NpgsqlCommand("select job_id from studio_asset_versions where id=$1", c);
            find.Parameters.AddWithValue(id);
            if (await find.ExecuteScalarAsync(token) is not Guid job) return;
            var version = new StudioVersionStore(database, storage).Get(job, id)!;
            await using var original = await new StoredVersionRecovery(storage, environment).OpenAsync(version, token);
            // Seekable disk staging bounds memory for large originals and remote streams.
            string temp = Path.Combine(Path.GetTempPath(), "studio-preview-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var staged = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await original.CopyToAsync(staged, token); staged.Position = 0;
                var info = await Image.IdentifyAsync(staged, token);
                if ((long)info.Width * info.Height > 60_000_000)
                { await SetState(id, "Unsupported", null, null, "Preview exceeds the supported pixel limit; download the original.", token); return; }
                staged.Position = 0;
                var configuration=SixLabors.ImageSharp.Configuration.Default.Clone();configuration.MaxDegreeOfParallelism=1;
                var decoder=new DecoderOptions{Configuration=configuration};
                using var image = await Image.LoadAsync(decoder,staged, token);
                image.Mutate(x => x.AutoOrient());
                string thumb = $"MediaDerivatives/{job:N}/{id:N}/thumbnail.jpg";
                string preview = $"MediaDerivatives/{job:N}/{id:N}/preview.jpg";
                await Write(image, 384, thumb, token);
                await Write(image, 1600, preview, token);
                await SetState(id, "Ready", thumb, preview, null, token);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (UnknownImageFormatException) { await SetState(id,"Unsupported",null,null,"Preview not available for this file format. The original can still be downloaded.",token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogWarning(error, "Preview generation failed for version {VersionId}; original is retained", id);
            await SetState(id, "Failed", null, null, "Preview unavailable. Retry generation or download the original.", token);
        }
    }
    private async Task Write(Image original, int edge, string key, CancellationToken token)
    {
        using var resized = original.Clone(x => x.Resize(new ResizeOptions { Mode=ResizeMode.Max, Size=new Size(edge,edge) }));
        resized.Metadata.ExifProfile=null; resized.Metadata.IptcProfile=null; resized.Metadata.XmpProfile=null;
        using var encoded = new MemoryStream();
        await resized.SaveAsJpegAsync(encoded,new JpegEncoder { Quality=80 },token); encoded.Position=0;
        await storage.StoreAsync(new StorageWriteRequest(key, encoded, "image/jpeg", "preview.jpg", "Studio preview worker"),token);
        await using var verify=await storage.OpenReadAsync(key,token);
        if(verify.ReadByte()<0)throw new IOException("Preview storage verification failed.");
    }
    private async Task SetState(Guid id,string state,string? thumb,string? preview,string? error,CancellationToken token)
    {
        await using var c=database.OpenConnection();
        await using var cmd=new NpgsqlCommand("""
            update studio_media_derivatives set status=$2,thumbnail_key=$3,preview_key=$4,last_error=$5,
              next_attempt_at=now()+make_interval(secs=>least(3600,30*power(2,least(attempts,7)))::int),updated_at=now()
            where version_id=$1
            """,c);
        foreach(var value in new object?[]{id,state,thumb,preview,error})cmd.Parameters.AddWithValue(value??DBNull.Value);
        await cmd.ExecuteNonQueryAsync(token);
    }
}
