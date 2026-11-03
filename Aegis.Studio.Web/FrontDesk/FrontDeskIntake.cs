using System.Text.Json;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.FrontDesk;

public sealed record FrontDeskIntake(Guid ConversationId, Guid? JobId, DateTimeOffset? ReceivedAtUtc, string Source);
public sealed record FrontDeskReceivedRange(DateOnly? From, DateOnly? To)
{
    public bool Includes(DateTimeOffset? timestamp, TimeZoneInfo zone) => timestamp.HasValue
        && (!From.HasValue || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp.Value, zone).DateTime) >= From)
        && (!To.HasValue || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp.Value, zone).DateTime) <= To);

    public static FrontDeskReceivedRange Resolve(string? preset, DateOnly? from, DateOnly? to, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var range = preset switch
        {
            null or "" or "any" => new FrontDeskReceivedRange(null, null),
            "today" => new(today, today), "yesterday" => new(today.AddDays(-1), today.AddDays(-1)),
            "7days" => new(today.AddDays(-6), today), "30days" => new(today.AddDays(-29), today),
            "month" => new(new(today.Year, today.Month, 1), new DateOnly(today.Year,today.Month,1).AddMonths(1).AddDays(-1)),
            "custom" when from.HasValue && to.HasValue => new(from, to),
            _ => throw new ArgumentException("Choose a received date filter; custom ranges require From and To dates.")
        };
        if (range.From > range.To) throw new ArgumentException("From must be on or before To.");
        return range;
    }
}

public sealed class FrontDeskIntakeStore(StudioDatabase database)
{
    public static string Source(string? channel, string? payload)
    {
        if (payload is not null)
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return BookingSources.Normalize(channel);
            // A conversion retains the original enquiry channel, including Unknown.
            if (root.TryGetProperty("conversationId", out var id) && id.ValueKind == JsonValueKind.String)
                return BookingSources.Normalize(channel);
            if (root.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String)
                return BookingSources.Normalize(source.GetString());
            if (root.TryGetProperty("bookingSource", out var provider) && provider.ValueKind == JsonValueKind.String)
            {
                var known = BookingSources.Normalize(provider.GetString());
                if (known != "Unknown") return known;
            }
        }
        return BookingSources.Normalize(channel);
    }

    public IReadOnlyList<FrontDeskIntake> List(string? received, DateOnly? from, DateOnly? to, string? source, TimeZoneInfo zone, DateTimeOffset now)
    {
        var range = FrontDeskReceivedRange.Resolve(received, from, to, now, zone);
        if (!string.IsNullOrEmpty(source)) BookingSources.RequireSelectable(source);
        using var connection = database.OpenConnection();
        using var command = new NpgsqlCommand("""
            select c.id,j.id,
                case when c.channel='Email' then least(c.created_at_utc,
                    (select min(m.created_at_utc) from studio_messages m where m.conversation_id=c.id and m.visibility='External'))
                    else c.created_at_utc end,
                c.channel, r.payload::text
            from studio_conversations c
            left join studio_jobs j on j.conversation_id=c.id
            left join lateral (select payload from studio_frontdesk_requests f where f.job_id=j.id order by f.created_at_utc limit 1) r on true
            where not c.is_closed or j.id is not null
            """, connection);
        using var reader = command.ExecuteReader();
        var result = new List<FrontDeskIntake>();
        while (reader.Read())
        {
            var entry = new FrontDeskIntake(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                Source(reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
            if (!string.IsNullOrEmpty(source) && source != entry.Source) continue;
            if ((range.From.HasValue || range.To.HasValue) && !range.Includes(entry.ReceivedAtUtc, zone)) continue;
            result.Add(entry);
        }
        return result.OrderBy(x => x.ReceivedAtUtc is null).ThenByDescending(x => x.ReceivedAtUtc).ThenBy(x => x.ConversationId).ToArray();
    }
}
