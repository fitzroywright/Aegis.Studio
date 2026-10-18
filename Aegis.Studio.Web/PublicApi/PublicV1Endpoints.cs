using System.Net;
using System.Text.Json;
using Aegis.Studio.Web.Finance;
using Aegis.Studio.Web.Galleries;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Versioning;
using Aegis.Studio.Web.Workflow;
using Common.Secrets;
using Common.Storage;

namespace Aegis.Studio.Web.PublicApi;

public static class PublicV1Endpoints
{
    public static void MapPublicV1(this WebApplication app)
    {
        var db = app.Services.GetRequiredService<StudioDatabase>();
        var store = new PublicV1Store(db); store.EnsureSchema();
        var portal = new ClientPortalStore(db); portal.EnsureSchema();
        var security = new PublicV1Security(app.Configuration, app.Services.GetRequiredService<ISecretProvider>(), db);
        var group = app.MapGroup("/api/public/v1").AllowAnonymous();
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                if (!http.Request.IsHttps && !(app.Environment.IsDevelopment() && http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip)))
                    throw new PublicApiException(400, "https_required", "Public V1 requires HTTPS.");
                return await next(ctx);
            }
            catch (PublicApiException e) { return Error(http, e.Status, e.Code, e.Message); }
            catch (JsonException) { return Error(http, 400, "invalid_json", "Supply a JSON object matching the V1 schema."); }
            catch (ArgumentException e) { return Error(http, 400, "invalid_request", e.Message); }
            catch (Aegis.Studio.Web.Media.StudioUnsupportedImageException) { return Error(http, 415, "proof_unsupported", "This media format cannot be presented as an image proof."); }
            catch (StorageException) { return Error(http, 503, "media_unavailable", "Media is temporarily unavailable. Retry later."); }
            catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
            {
                app.Logger.LogError(e,"Public V1 request failed {RequestId}",http.TraceIdentifier);
                return Error(http,503,"temporarily_unavailable","The operation is temporarily unavailable. Retry using the same idempotency key.");
            }
        });
        group.MapGet("/openapi.json", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "PublicApi", "public-v1.openapi.json"), "application/json"));
        group.MapPost("/bookings", async (HttpContext context, WorkspaceStore workspaces) =>
        {
            byte[] body = await Body(context);
            string client = await security.Integration(context, "bookings.write", body);
            var request = JsonSerializer.Deserialize<PublicBookingV1>(body, PublicV1Store.Json) ?? throw new JsonException();
            var result = store.CreateBooking(client, PublicV1Security.IdempotencyKey(context), request, workspaces, app.Configuration);
            return Results.Json(result, statusCode: 201);
        });
        group.MapGet("/bookings/{reference:guid}", async (Guid reference, HttpContext context) =>
            Results.Ok(store.Booking(await security.Integration(context, "bookings.read", []), reference)));
        group.MapGet("/galleries/{reference:guid}", (Guid reference, HttpContext context) =>
        {
            RequireGallery(portal, context, reference);
            int limit = 50;
            if (context.Request.Query.TryGetValue("limit", out var size) && !int.TryParse(size,out limit)) throw new ArgumentException("Invalid page limit.");
            if (limit < 1 || limit > 100) throw new ArgumentException("Limit must be between 1 and 100.");
            Guid? after = null;
            if (context.Request.Query.TryGetValue("after", out var cursor)) after = Guid.TryParse(cursor, out var id) ? id : throw new ArgumentException("Invalid page cursor.");
            using var c = db.OpenConnection(); using var tx = c.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
            Guid publication = PublicV1Store.Publication(c, tx, reference);
            var items = new List<PublicGalleryItemV1>();
            using (var q = WorkflowStore.Command(c, tx, """
                select v.id,v.file_name,coalesce(selection.selected,false)
                from studio_workflow_publications p join studio_workflow_submissions s on s.id=p.submission_id
                join studio_asset_versions v on v.job_id=p.job_id
                left join studio_public_v1_selections selection on selection.publication_id=p.id and selection.version_id=v.id
                where p.id=$1 and ($2::uuid is null or v.id>$2) and exists(select 1 from jsonb_array_elements(s.manifest) x where (x->>'versionId')::uuid=v.id)
                order by v.id limit $3
                """, publication, after, limit + 1))
            using (var r = q.ExecuteReader()) while (r.Read()) items.Add(new(r.GetGuid(0), r.GetString(1), r.GetBoolean(2)));
            bool more = items.Count > limit;
            if (more) items.RemoveAt(items.Count - 1);
            string title = (string)WorkflowStore.Scalar(c, tx, "select title from studio_jobs where id=$1", reference)!;
            bool release = FinanceReleaseStore.Decision(c, tx, reference).Authorized;
            return Results.Ok(new PublicGalleryPageV1(reference, publication, title, release, items, more ? items[^1].VersionId : null));
        });
        group.MapGet("/galleries/{reference:guid}/items/{version:guid}/{variant}", async (Guid reference, Guid version, string variant, HttpContext context, IFileStorage storage) =>
        {
            RequireGallery(portal, context, reference);
            if (!GalleryProofs.IsProofVariant(variant) && variant != "download") throw new PublicApiException(404, "not_found", "Unknown gallery variant.");
            using var c = db.OpenConnection(); using var tx = c.BeginTransaction();
            // Approval, revocation and release decisions serialize against workflow commands.
            WorkflowStore.Scalar(c, tx, "select id from studio_jobs where id=$1 for share", reference);
            Guid publication = PublicV1Store.Publication(c, tx, reference);
            if (!PublicV1Store.ContainsVersion(c, tx, publication, reference, version)) throw new PublicApiException(404, "not_found", "This version is not in the published approved gallery.");
            if (variant == "download" && !FinanceReleaseStore.Decision(c, tx, reference).Authorized)
                throw new PublicApiException(403, "payment_required", "Final delivery requires settlement or Finance authorization.");
            var versionStore = new StudioVersionStore(db, storage);
            var asset = versionStore.Get(reference, version) ?? throw new PublicApiException(404, "not_found", "Version unavailable.");
            var content = await storage.OpenReadAsync("VersionStore/" + asset.StoredPath, context.RequestAborted);
            if (variant == "download")
            {
                WorkflowStore.Sql(c, tx, "insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'CustomerFinalDownload','Customer',$3,now())", Guid.NewGuid(), reference, "Approved version " + version);
                tx.Commit();
                return Results.Stream(content, asset.ContentType, fileDownloadName: Path.GetFileName(asset.FileName), enableRangeProcessing: false);
            }
            await using (content)
                return Results.Bytes(GalleryProofs.Render(content, variant == "thumbnail"), "image/jpeg");
        });
        group.MapPut("/galleries/{reference:guid}/selections/{version:guid}", async (Guid reference, Guid version, HttpContext context) =>
        {
            RequireGallery(portal, context, reference);
            var request = JsonSerializer.Deserialize<PublicSelectionV1>(await Body(context), PublicV1Store.Json) ?? throw new JsonException();
            using var c = db.OpenConnection(); using var tx = c.BeginTransaction();
            WorkflowStore.Scalar(c, tx, "select id from studio_jobs where id=$1 for update", reference);
            Guid publication = PublicV1Store.Publication(c, tx, reference);
            if (!PublicV1Store.ContainsVersion(c, tx, publication, reference, version)) throw new PublicApiException(404, "not_found", "Version is not in this published gallery.");
            WorkflowStore.Sql(c, tx, "insert into studio_public_v1_selections(publication_id,version_id,selected) values($1,$2,$3) on conflict(publication_id,version_id) do update set selected=excluded.selected,updated_at_utc=now()", publication, version, request.Selected);
            tx.Commit(); return Results.Ok(new { versionId = version, request.Selected });
        });
        group.MapPost("/galleries/{reference:guid}/selections/submit", (Guid reference, HttpContext context) =>
        {
            RequireGallery(portal, context, reference);
            using var c = db.OpenConnection(); using var tx = c.BeginTransaction();
            WorkflowStore.Scalar(c, tx, "select id from studio_jobs where id=$1 for update", reference);
            Guid publication = PublicV1Store.Publication(c, tx, reference);
            var submission = PublicV1Store.SubmitSelections(c, tx, publication, PublicV1Security.IdempotencyKey(context));
            WorkflowStore.Notify(c, tx, reference, "public-selection:" + submission.Reference, "Owner", "Client selections submitted");
            WorkflowStore.Notify(c, tx, reference, "public-selection:" + submission.Reference, "Production Manager", "Client selections submitted");
            tx.Commit(); return Results.Ok(submission);
        });
        app.MapGet("/api/operations-management/webhooks", (System.Security.Claims.ClaimsPrincipal user, WorkflowStore workflow) =>
        {
            if(!workflow.IsOperations(user))return Results.Forbid();
            using var c=db.OpenConnection();
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c,null,"""
                select coalesce(jsonb_agg(to_jsonb(x)),'[]'::jsonb)::text from (
                  select e.id,e.event_type,d.destination_key,d.attempts,d.delivered_at_utc,d.next_attempt_utc,d.last_error,
                    case when e.event_type like 'booking.%' then r.job_id else e.resource_id end as job_id
                  from studio_public_webhook_events e join studio_public_webhook_deliveries d on d.event_id=e.id
                  left join studio_frontdesk_requests r on r.id=e.resource_id
                  order by e.occurred_at_utc desc limit 100) x
                """)!));
        }).RequireAuthorization();
        group.Map("/{**path}",(HttpContext context)=>Error(context,404,"not_found","Unknown V1 resource."));
        // Management can review the same exact versions chosen by the customer.
        app.MapGet("/api/jobs/{job:guid}/customer-selections", (Guid job, System.Security.Claims.ClaimsPrincipal user, JobAccessService access) =>
        {
            if (!access.HasPermission(user, job, "ViewJob")) return Results.Forbid();
            using var c = db.OpenConnection();
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>((string)WorkflowStore.Scalar(c, null, "select coalesce(jsonb_agg(s.response order by s.id),'[]'::jsonb)::text from studio_public_v1_selection_submissions s join studio_workflow_publications p on p.id=s.publication_id where p.job_id=$1", job)!));
        }).RequireAuthorization();
    }
    private static void RequireGallery(ClientPortalStore portal, HttpContext context, Guid job)
    {
        string header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || portal.Validate(header[7..]) is not { } access || access.JobId != job)
            throw new PublicApiException(401, "invalid_gallery_access", "A valid client gallery access token for this Job is required.");
    }
    private static IResult Error(HttpContext context, int status, string code, string message)
        => Results.Json(new PublicV1Error(code, message, context.TraceIdentifier), statusCode: status);
    private static async Task<byte[]> Body(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new PublicApiException(415, "json_required", "Content-Type application/json is required.");
        using var buffer = new MemoryStream();
        var chunk = new byte[8192]; int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + count > 65536) throw new PublicApiException(413, "request_too_large", "JSON body exceeds 64 KiB.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }
}

