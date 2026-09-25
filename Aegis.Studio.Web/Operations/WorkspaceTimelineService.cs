using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class WorkspaceTimelineService
{
    private readonly StudioDatabase database;
    private readonly StudioStore studio;

    public WorkspaceTimelineService(StudioDatabase database, StudioStore studio)
    {
        this.database = database;
        this.studio = studio;
    }

    public IReadOnlyList<StudioTimelineItem> GetForJob(Guid jobId, int count = 200)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null)
        {
            return [];
        }

        List<StudioTimelineItem> items = [];
        items.Add(new StudioTimelineItem(
            job.Id,
            job.CreatedAt,
            "JobCreated",
            "Job created",
            job.Title,
            null,
            "Job",
            job.Id.ToString("D")));

        AddConversationMessages(job.ConversationId, items);
        AddAttachments(job.ConversationId, items);
        AddOperationalAudit(jobId, items);

        return Order(items, count);
    }

    public IReadOnlyList<StudioTimelineItem> GetForWorkspace(Guid workspaceId, int count = 200)
    {
        List<StudioTimelineItem> items = [];

        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select id,subject,contact_name,created_at_utc
                from studio_conversations
                where workspace_id=$1
                order by created_at_utc desc
                limit 200
                """;
            command.Parameters.AddWithValue(workspaceId);

            using NpgsqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new StudioTimelineItem(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    "ConversationCreated",
                    "Conversation opened",
                    $"{reader.GetString(2)} · {reader.GetString(1)}",
                    null,
                    "Conversation",
                    reader.GetGuid(0).ToString("D")));
            }
        }

        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select id,title,client_name,created_at_utc
                from studio_jobs
                where workspace_id=$1
                order by created_at_utc desc
                limit 200
                """;
            command.Parameters.AddWithValue(workspaceId);

            using NpgsqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new StudioTimelineItem(
                    reader.GetGuid(0),
                    reader.GetFieldValue<DateTimeOffset>(3),
                    "JobCreated",
                    "Job created",
                    $"{reader.GetString(2)} · {reader.GetString(1)}",
                    null,
                    "Job",
                    reader.GetGuid(0).ToString("D")));
            }
        }

        return Order(items, count);
    }

    private void AddConversationMessages(Guid conversationId, List<StudioTimelineItem> items)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,author_user_name,author_display_name,visibility,body,created_at_utc
            from studio_messages
            where conversation_id=$1
            order by created_at_utc desc
            limit 200
            """;
        command.Parameters.AddWithValue(conversationId);

        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string visibility = reader.GetString(3);
            string body = reader.GetString(4);
            items.Add(new StudioTimelineItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(5),
                visibility.Equals("Internal", StringComparison.OrdinalIgnoreCase) ? "InternalMessage" : "Message",
                visibility.Equals("Internal", StringComparison.OrdinalIgnoreCase) ? "Internal note" : "Message",
                Preview(body),
                reader.IsDBNull(1) ? reader.GetString(2) : reader.GetString(1),
                "Conversation",
                conversationId.ToString("D")));
        }
    }

    private void AddAttachments(Guid conversationId, List<StudioTimelineItem> items)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,file_name,visibility,created_at_utc
            from studio_attachments
            where conversation_id=$1
            order by created_at_utc desc
            limit 200
            """;
        command.Parameters.AddWithValue(conversationId);

        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new StudioTimelineItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(3),
                "FileAdded",
                "File added",
                $"{reader.GetString(1)} · {reader.GetString(2)}",
                null,
                "Attachment",
                reader.GetGuid(0).ToString("D")));
        }
    }

    private void AddOperationalAudit(Guid jobId, List<StudioTimelineItem> items)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,action,user_name,detail,created_at_utc
            from studio_operational_audit
            where job_id=$1
            order by created_at_utc desc
            limit 200
            """;
        command.Parameters.AddWithValue(jobId);

        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string action = reader.GetString(1);
            items.Add(new StudioTimelineItem(
                reader.GetGuid(0),
                reader.GetFieldValue<DateTimeOffset>(4),
                "Operational",
                Humanize(action),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(2),
                "Job",
                jobId.ToString("D")));
        }
    }

    private static IReadOnlyList<StudioTimelineItem> Order(List<StudioTimelineItem> items, int count)
        => items
            .OrderByDescending(x => x.OccurredAtUtc)
            .Take(Math.Clamp(count, 1, 500))
            .ToArray();

    private static string Preview(string value)
    {
        string normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 240 ? normalized : normalized[..237] + "...";
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Operational event";
        }

        string text = value.Replace(":", " · ", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
    }
}

public sealed record StudioTimelineItem(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string Kind,
    string Title,
    string? Detail,
    string? Actor,
    string ReferenceType,
    string ReferenceId);
