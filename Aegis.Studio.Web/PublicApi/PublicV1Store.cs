using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Npgsql;

namespace Aegis.Studio.Web.PublicApi;

public sealed record PublicBookingV1([property: System.Text.Json.Serialization.JsonRequired] string ClientName, [property: System.Text.Json.Serialization.JsonRequired] string ClientEmail, [property: System.Text.Json.Serialization.JsonRequired] string SessionType, [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset StartsAt, [property: System.Text.Json.Serialization.JsonRequired] DateTimeOffset EndsAt, string? ClientPhone = null, string? Location = null, string? Notes = null, string? Destination = null);
public sealed record PublicBookingReceiptV1(Guid Reference, string Status, decimal RequiredDeposit, string Currency);
public sealed record PublicGalleryItemV1(Guid VersionId, string FileName, bool Selected);
public sealed record PublicGalleryPageV1(Guid Reference, Guid PublicationId, string Title, bool FinalDeliveryAvailable, IReadOnlyList<PublicGalleryItemV1> Items, Guid? NextCursor);
public sealed record PublicSelectionV1([property: System.Text.Json.Serialization.JsonRequired] bool Selected);
public sealed record PublicSelectionSubmissionV1(Guid Reference, Guid PublicationId, IReadOnlyList<Guid> VersionIds, DateTimeOffset SubmittedAt);

public sealed class PublicV1Store(StudioDatabase database)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void EnsureSchema()
    {
        using var c = database.OpenConnection();
        WorkflowStore.Sql(c, null, """
            create table if not exists studio_public_v1_bookings(
              client_key text not null,idempotency_key text not null,fingerprint text not null,booking_id uuid not null unique references studio_frontdesk_requests(id),
              receipt jsonb not null,primary key(client_key,idempotency_key));
            create table if not exists studio_public_v1_selections(
              publication_id uuid not null references studio_workflow_publications(id),version_id uuid not null references studio_asset_versions(id),
              selected boolean not null,updated_at_utc timestamptz not null default now(),primary key(publication_id,version_id));
            create table if not exists studio_public_v1_selection_submissions(
              id uuid primary key,publication_id uuid not null references studio_workflow_publications(id),idempotency_key text not null,response jsonb not null,
              unique(publication_id,idempotency_key));
            """);
    }
    public PublicBookingReceiptV1 CreateBooking(string client, string key, PublicBookingV1 request, WorkspaceStore workspaces, IConfiguration configuration)
    {
        string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, Json)));
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        WorkflowStore.Sql(c, tx, "select pg_advisory_xact_lock(hashtextextended($1,0))", "public-v1:" + client + ":" + key);
        using (var q = WorkflowStore.Command(c, tx, "select fingerprint,receipt::text from studio_public_v1_bookings where client_key=$1 and idempotency_key=$2", client, key))
        using (var r = q.ExecuteReader())
        {
            if (r.Read())
            {
                if (r.GetString(0) != hash) throw new PublicApiException(409, "idempotency_conflict", "This key was used for different booking details.");
                return JsonSerializer.Deserialize<PublicBookingReceiptV1>(r.GetString(1), Json)!;
            }
        }
        if (request.StartsAt == default || request.EndsAt <= request.StartsAt) throw new ArgumentException("Choose a valid session start and end.");
        var workspace = string.IsNullOrWhiteSpace(request.Destination) ? null : workspaces.FindByRouteKey(request.Destination) ?? workspaces.FindByAddress(request.Destination);
        if (workspace is null || !workspace.IsEnabled) workspace = workspaces.Get(WorkflowRules.UnassignedWorkspaceId) ?? throw new InvalidOperationException("Unassigned Workspace unavailable.");
        // Recovery after interruption is handled by the intake store's stable external ID.
        string external = "v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client + ":" + key)));
        var pending = new PendingBookingRequest(workspace.Id, request.ClientName, request.ClientEmail, request.SessionType,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.StartsAt, TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica")).DateTime),
            Math.Max(0, configuration.GetValue<decimal?>("BookingApi:RequiredDeposit") ?? 0), "JMD", request.Notes,
            ExternalBookingId: external, StartsAtUtc: request.StartsAt.ToUniversalTime(), EndsAtUtc: request.EndsAt.ToUniversalTime(),
            Location: request.Location, ClientPhone: request.ClientPhone, Destination: request.Destination, BookingSource: "WebsiteV1");
        var booking = new FrontDeskBookingRequestStore(database).Create(pending, "Website V1: " + client);
        var receipt = new PublicBookingReceiptV1(booking.Id, "received", pending.RequiredDeposit, "JMD");
        WorkflowStore.Sql(c, tx, "insert into studio_public_v1_bookings values($1,$2,$3,$4,$5::jsonb)", client, key, hash, booking.Id, JsonSerializer.Serialize(receipt, Json));
        tx.Commit(); return receipt;
    }
    public PublicBookingReceiptV1 Booking(string client, Guid reference)
    {
        using var c = database.OpenConnection();
        using var q = WorkflowStore.Command(c, null, "select j.status,r.payload->>'requiredDeposit' from studio_public_v1_bookings b join studio_frontdesk_requests r on r.id=b.booking_id join studio_jobs j on j.id=r.job_id where b.client_key=$1 and b.booking_id=$2", client, reference);
        using var r = q.ExecuteReader();
        if (!r.Read()) throw new PublicApiException(404, "not_found", "Booking reference not found.");
        return new(reference, ExternalStatus(r.GetString(0)), decimal.Parse(r.GetString(1), System.Globalization.CultureInfo.InvariantCulture), "JMD");
    }
    public static string ExternalStatus(string stage) => stage switch
    {
        "Pending" => "received", "Cancelled" => "cancelled", "Completed" => "completed", "Delivered" => "delivered",
        "Ready for Delivery" => "ready", _ => "confirmed"
    };
    public static Guid Publication(NpgsqlConnection c, NpgsqlTransaction? tx, Guid job)
    {
        object? id = WorkflowStore.Scalar(c, tx, """
            select p.id from studio_workflow_publications p join studio_workflow_submissions s on s.id=p.submission_id join studio_jobs j on j.id=p.job_id
            where p.job_id=$1 and s.kind='Finished' and s.status='Approved' and j.status in ('Ready for Delivery','Delivered','Completed')
            order by p.created_at_utc desc,p.id desc limit 1
            """, job);
        return id is Guid value ? value : throw new PublicApiException(404, "gallery_unavailable", "No approved published gallery is available.");
    }
    public static bool ContainsVersion(NpgsqlConnection c, NpgsqlTransaction? tx, Guid publication, Guid job, Guid version)
        => WorkflowStore.Exists(c, tx, """
            select exists(select 1 from studio_workflow_publications p join studio_workflow_submissions s on s.id=p.submission_id
              join studio_asset_versions v on v.job_id=p.job_id and v.id=$3
              where p.id=$1 and p.job_id=$2 and exists(select 1 from jsonb_array_elements(s.manifest) x where (x->>'versionId')::uuid=v.id))
            """, publication, job, version);
    public static PublicSelectionSubmissionV1 SubmitSelections(NpgsqlConnection c, NpgsqlTransaction tx, Guid publication, string key)
    {
        string? prior = WorkflowStore.Scalar(c, tx, "select response::text from studio_public_v1_selection_submissions where publication_id=$1 and idempotency_key=$2", publication, key) as string;
        if (prior is not null) return JsonSerializer.Deserialize<PublicSelectionSubmissionV1>(prior, Json)!;
        var selected = new List<Guid>();
        using (var q = WorkflowStore.Command(c, tx, "select version_id from studio_public_v1_selections where publication_id=$1 and selected order by version_id", publication))
        using (var r = q.ExecuteReader()) while (r.Read()) selected.Add(r.GetGuid(0));
        if (selected.Count == 0) throw new PublicApiException(400, "selection_required", "Select at least one proof before submitting.");
        var result = new PublicSelectionSubmissionV1(Guid.NewGuid(), publication, selected, DateTimeOffset.UtcNow);
        WorkflowStore.Sql(c, tx, "insert into studio_public_v1_selection_submissions values($1,$2,$3,$4::jsonb)", result.Reference, publication, key, JsonSerializer.Serialize(result, Json));
        return result;
    }
}
