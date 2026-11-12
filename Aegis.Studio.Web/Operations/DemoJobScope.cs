using Npgsql;
namespace Aegis.Studio.Web.Operations;

// Demo ownership follows explicit seed provenance, not a human-readable title or
// the UUID assigned by a normal booking-to-job conversion.
internal static class DemoJobScope
{
    internal const string Predicate = """
        (j.id::text like 'de000000-%' or j.id::text like 'de100000-%'
         or j.conversation_id::text like 'de000000-%' or j.conversation_id::text like 'de100000-%'
         or exists(select 1 from studio_frontdesk_requests r where r.job_id=j.id
             and (r.created_by='demo-seed' or r.id::text like 'de000000-%' or r.id::text like 'de100000-%'))
         or exists(select 1 from studio_bookings b where b.job_id=j.id
             and (b.id::text like 'de000000-%' or b.id::text like 'de100000-%' or b.external_booking_id like 'DEMO-%')))
        """;

    internal static List<(Guid Id, string Title)> Read(NpgsqlConnection connection)
    {
        using var command = new NpgsqlCommand("select j.id,j.title from studio_jobs j where " + Predicate + " order by j.id", connection);
        using var reader = command.ExecuteReader();
        var jobs = new List<(Guid Id, string Title)>();
        while (reader.Read()) jobs.Add((reader.GetGuid(0), reader.GetString(1)));
        return jobs;
    }
}
