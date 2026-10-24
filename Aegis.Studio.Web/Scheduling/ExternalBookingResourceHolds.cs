using Npgsql;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Aegis.Studio.Web.Scheduling;

/// <summary>
/// Keeps API-reserved bookings visible to Studio scheduling before a staff member
/// turns the Pending FrontDesk request into its normal schedule entries.
/// </summary>
public static class ExternalBookingResourceHolds
{
    public static void EnsureSchema(StudioDatabase database)
    {
        using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("""
            create table if not exists studio_external_booking_holds(
              id uuid primary key default gen_random_uuid(),
              external_booking_id text not null unique,
              job_id uuid not null references studio_jobs(id),
              title text not null,
              location text null,
              location_code text null,
              starts_at_utc timestamptz not null,
              ends_at_utc timestamptz not null,
              busy_starts_at_utc timestamptz not null,
              busy_ends_at_utc timestamptz not null,
              resource_ids uuid[] not null,
              status text not null default 'Active' check(status in ('Active','Released')),
              created_at_utc timestamptz not null default now(),
              released_at_utc timestamptz null,
              released_by text null,
              release_reason text null,
              check(ends_at_utc>starts_at_utc),
              check(busy_ends_at_utc>busy_starts_at_utc)
            );
            create index if not exists ix_external_booking_holds_time
              on studio_external_booking_holds(busy_starts_at_utc,busy_ends_at_utc) where status='Active';
            """,c);
        q.ExecuteNonQuery();
        using var migrate=new NpgsqlCommand("alter table studio_external_booking_holds add column if not exists location_code text; alter table studio_external_booking_holds add column if not exists released_at_utc timestamptz; alter table studio_external_booking_holds add column if not exists released_by text; alter table studio_external_booking_holds add column if not exists release_reason text",c);
        migrate.ExecuteNonQuery();
    }

    public static bool Release(StudioDatabase database,string externalBookingId,string actor,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason)||reason.Trim().Length>500)throw new ArgumentException("A release reason of at most 500 characters is required.");
        using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("update studio_external_booking_holds set status='Released',released_at_utc=now(),released_by=$2,release_reason=$3 where external_booking_id=$1 and status='Active'",c);
        q.Parameters.AddWithValue(externalBookingId);q.Parameters.AddWithValue(actor);q.Parameters.AddWithValue(reason.Trim());
        return q.ExecuteNonQuery()==1;
    }

    public static void MapAdminEndpoints(WebApplication app)
    {
        app.MapPost("/api/admin/public-booking/holds/{bookingId:guid}/release",
            (Guid bookingId,ReleaseExternalBookingHold request,System.Security.Claims.ClaimsPrincipal user,StudioDatabase database,IConfiguration configuration)=>
            {
                string origin=new Uri(configuration["PublicSite:BaseUrl"]??throw new InvalidOperationException("Public Site is not configured.")).GetLeftPart(UriPartial.Authority);
                try
                {
                    IResult result=Release(database,origin+":"+bookingId, user.Identity?.Name??"Studio Administrator",request.Reason)
                        ?Results.Ok(new{status="Released"}):Results.NotFound(new{error="Active booking hold was not found."});
                    return result;
                }
                catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
            }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute{Roles="Administrator"});
    }

    public sealed record ReleaseExternalBookingHold(string Reason);

    public static void CreateForImportedBooking(NpgsqlConnection c,NpgsqlTransaction tx,string externalBookingId,Guid jobId,
        string title,string? location,string? locationCode,DateTimeOffset startsAt,DateTimeOffset endsAt,
        int setupBufferMinutes,int cleanupBufferMinutes,IReadOnlyDictionary<string,int> requirements)
    {
        if(string.IsNullOrWhiteSpace(externalBookingId)||endsAt<=startsAt)throw new ArgumentException("External booking hold needs a stable identity and valid time range.");
        if(setupBufferMinutes is <0 or >480||cleanupBufferMinutes is <0 or >480)throw new ArgumentException("Invalid booking setup or cleanup buffer.");

        var resourceTypes=new Dictionary<Guid,string>();
        using(var q=new NpgsqlCommand("select id,resource_type from studio_schedule_resources where is_active order by id",c,tx))
        using(var r=q.ExecuteReader())while(r.Read())resourceTypes.Add(r.GetGuid(0),r.GetString(1));
        var photographerIds=new HashSet<Guid>();
        using(var q=new NpgsqlCommand("select schedule_resource_id from studio_photographers where is_active",c,tx))
        using(var r=q.ExecuteReader())while(r.Read())photographerIds.Add(r.GetGuid(0));

        // Lock the full candidate set before checking either scheduled entries or other holds.
        // ScheduleStore uses the same advisory lock namespace.
        var candidatesByType=new Dictionary<string,Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var requirement in requirements.Where(x=>x.Value>0).OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))
        {
            Guid[] candidates=(requirement.Key.Equals("Photographer",StringComparison.OrdinalIgnoreCase)
                ?resourceTypes.Keys.Where(photographerIds.Contains)
                :resourceTypes.Where(x=>x.Value.Equals(requirement.Key,StringComparison.OrdinalIgnoreCase)).Select(x=>x.Key))
                .Order().ToArray();
            candidatesByType[requirement.Key]=candidates;
        }
        foreach(Guid id in candidatesByType.Values.SelectMany(x=>x).Distinct().Order())
        {using var l=new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))",c,tx);l.Parameters.AddWithValue(id.ToString());l.ExecuteNonQuery();}

        DateTimeOffset busyStart=startsAt.AddMinutes(-setupBufferMinutes).ToUniversalTime();
        DateTimeOffset busyEnd=endsAt.AddMinutes(cleanupBufferMinutes).ToUniversalTime();
        var assigned=new HashSet<Guid>();
        foreach(var requirement in requirements.Where(x=>x.Value>0).OrderByDescending(x=>x.Key.Equals("Photographer",StringComparison.OrdinalIgnoreCase)).ThenBy(x=>x.Key,StringComparer.OrdinalIgnoreCase))
        {
            if(!candidatesByType.TryGetValue(requirement.Key,out var candidates))throw new ArgumentException("Booking resource requirements are invalid.");
            int count=0;
            foreach(Guid id in candidates)
            {
                if(assigned.Contains(id)||!IsFree(c,tx,id,busyStart,busyEnd))continue;
                if(photographerIds.Contains(id)&&IsOnLeave(c,tx,id,busyStart,busyEnd))continue;
                assigned.Add(id);if(++count==requirement.Value)break;
            }
            if(count<requirement.Value)
                throw new InvalidOperationException($"The API reserved this booking, but Studio could not hold enough '{requirement.Key}' resources at the selected time. Resolve the schedule conflict before acknowledging the booking import.");
        }

        using var save=new NpgsqlCommand("""
            insert into studio_external_booking_holds(external_booking_id,job_id,title,location,location_code,starts_at_utc,ends_at_utc,busy_starts_at_utc,busy_ends_at_utc,resource_ids,status,released_at_utc)
            values($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,'Active',null)
            on conflict(external_booking_id) do update set job_id=excluded.job_id,title=excluded.title,location=excluded.location,location_code=excluded.location_code,
              starts_at_utc=excluded.starts_at_utc,ends_at_utc=excluded.ends_at_utc,busy_starts_at_utc=excluded.busy_starts_at_utc,
              busy_ends_at_utc=excluded.busy_ends_at_utc,resource_ids=excluded.resource_ids,status='Active',released_at_utc=null
            where studio_external_booking_holds.job_id=excluded.job_id
            """,c,tx);
        save.Parameters.AddWithValue(externalBookingId);save.Parameters.AddWithValue(jobId);save.Parameters.AddWithValue(title);
        save.Parameters.AddWithValue((object?)location??DBNull.Value);save.Parameters.AddWithValue((object?)locationCode??DBNull.Value);save.Parameters.AddWithValue(startsAt.ToUniversalTime());save.Parameters.AddWithValue(endsAt.ToUniversalTime());
        save.Parameters.AddWithValue(busyStart);save.Parameters.AddWithValue(busyEnd);save.Parameters.AddWithValue(assigned.Order().ToArray());
        if(save.ExecuteNonQuery()!=1)throw new ArgumentException("External booking identity is already linked to another Studio job.");
    }

    static bool IsFree(NpgsqlConnection c,NpgsqlTransaction tx,Guid resource,DateTimeOffset start,DateTimeOffset end)
    {
        using var q=new NpgsqlCommand("""
            select not exists(select 1 from studio_schedule_entries e join studio_schedule_entry_resources er on er.entry_id=e.id
              where er.resource_id=$1 and e.status<>'Cancelled' and coalesce(er.attendance_start_utc,e.starts_at_utc)<$3 and coalesce(er.attendance_end_utc,e.ends_at_utc)>$2)
             and not exists(select 1 from studio_external_booking_holds h join studio_jobs j on j.id=h.job_id
              where h.status='Active' and j.status not in ('Cancelled','Completed') and $1=any(h.resource_ids) and h.busy_starts_at_utc<$3 and h.busy_ends_at_utc>$2)
            """,c,tx);
        q.Parameters.AddWithValue(resource);q.Parameters.AddWithValue(start);q.Parameters.AddWithValue(end);return (bool)q.ExecuteScalar()!;
    }

    static bool IsOnLeave(NpgsqlConnection c,NpgsqlTransaction tx,Guid resource,DateTimeOffset start,DateTimeOffset end)
    {
        using var q=new NpgsqlCommand("select exists(select 1 from studio_photographers p join studio_photographer_availability a on lower(a.user_name)=lower(p.user_name) where p.schedule_resource_id=$1 and a.availability_type in ('Unavailable','Leave') and a.starts_at_utc<$3 and a.ends_at_utc>$2)",c,tx);
        q.Parameters.AddWithValue(resource);q.Parameters.AddWithValue(start);q.Parameters.AddWithValue(end);return (bool)q.ExecuteScalar()!;
    }
}
