using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailStore
{
    private readonly StudioDatabase database;
    private readonly WorkspaceStore workspaces;

    public OutboundEmailStore(StudioDatabase database, WorkspaceStore workspaces)
    {
        this.database = database;
        this.workspaces = workspaces;
    }

    public OutboundEmail Queue(
        StudioConversation conversation,
        string body,
        StudioSendingIdentity? sendingIdentity = null)
        => QueueMessage(
            conversation,
            body,
            conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : "Re: " + conversation.Subject,
            sendingIdentity: sendingIdentity);

    public OutboundEmail QueueNotification(StudioConversation conversation, string subject, string body)
        => QueueMessage(conversation, body, subject, "Notification");

    public OutboundEmail? QueueAcknowledgement(StudioConversation conversation, string workspaceName)
    {
        if (ExistsAcknowledgement(conversation.Id)) return null;
        string jobReference = "AEGIS-" + conversation.Id.ToString("N")[..8].ToUpperInvariant();
        string body = $"Hello {conversation.ContactName},\n\nWe received your message and it has been accepted into the {workspaceName} workspace. Our team will handle it from here.\n\nReference: {jobReference}\nPlease keep this reference in the subject when starting a separate email about the same request.\n\nBryan's Studio";
        string subject = $"[{jobReference}] We received your message - {conversation.Subject}";
        return QueueMessage(conversation, body, subject, "Acknowledgement");
    }

    public IReadOnlyList<OutboundEmail> GetPending()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + """
            where status in ('Pending','Retry')
              and attempts < 10
              and (next_attempt_at_utc is null or next_attempt_at_utc <= now())
            order by created_at_utc
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<OutboundEmail> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public OutboundEmail? MarkProviderAccepted(Guid id, string? providerMessageId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_outbound_email
            set status='ProviderAccepted',
                provider_message_id=$2,
                attempts=attempts+1,
                last_error=null,
                next_attempt_at_utc=null,
                provider_accepted_at_utc=now()
            where id=$1
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue((object?)providerMessageId?.Trim() ?? DBNull.Value);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public OutboundEmail? MarkFailure(Guid id, string failureCode, DateTimeOffset nextAttemptAtUtc)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_outbound_email
            set attempts=attempts+1,
                status=case when attempts+1 >= 10 then 'Failed' else 'Retry' end,
                last_error=$2,
                next_attempt_at_utc=case when attempts+1 >= 10 then null else $3 end
            where id=$1
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(failureCode) ? "ProviderFailure" : failureCode.Trim());
        command.Parameters.AddWithValue(nextAttemptAtUtc);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    private OutboundEmail? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    private bool ExistsAcknowledgement(Guid conversationId)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select exists(select 1 from studio_outbound_email where conversation_id=$1 and lower(kind)='acknowledgement')";command.Parameters.AddWithValue(conversationId);return (bool)(command.ExecuteScalar()??false);
    }

    private OutboundEmail QueueMessage(
        StudioConversation conversation,
        string body,
        string subject,
        string kind = "Reply",
        StudioSendingIdentity? sendingIdentity = null)
    {
        StudioWorkspaceDefinition workspace = workspaces.Get(conversation.WorkspaceId) ?? throw new InvalidOperationException("The conversation workspace no longer exists.");
        if (string.IsNullOrWhiteSpace(workspace.EmailAddress)) throw new InvalidOperationException("The conversation workspace has no outbound email identity configured.");

        StudioSendingIdentity identity = sendingIdentity ??
            new StudioSendingIdentity(StudioSendingIdentityType.Workspace, workspace.EmailAddress, null);

        OutboundEmail message = new(
            Guid.NewGuid(),
            conversation.Id,
            identity.FromAddress,
            conversation.ContactAddress,
            subject,
            body.Trim(),
            conversation.ExternalThreadId,
            "Pending",
            DateTimeOffset.UtcNow,
            null,
            kind,
            identity.UserName,
            identity.Type.ToString(),
            0,
            null,
            null,
            null);

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_outbound_email
            (id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind,sent_by_user_name,sending_identity_type)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            """;
        object?[] values =
        [
            message.Id,
            message.ConversationId,
            message.FromAddress,
            message.ToAddress,
            message.Subject,
            message.Body,
            (object?)message.ThreadId ?? DBNull.Value,
            message.Status,
            message.CreatedAt,
            (object?)message.ProviderMessageId ?? DBNull.Value,
            message.Kind,
            (object?)message.SentByUserName ?? DBNull.Value,
            message.SendingIdentityType
        ];
        foreach (object? value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        command.ExecuteNonQuery();
        return message;
    }

    private static OutboundEmail Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12),
        r.GetInt32(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16));

    private const string SelectSql =
        "select id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind,sent_by_user_name,sending_identity_type,attempts,last_error,next_attempt_at_utc,provider_accepted_at_utc from studio_outbound_email ";
}

public sealed record OutboundEmail(
    Guid Id,
    Guid ConversationId,
    string FromAddress,
    string ToAddress,
    string Subject,
    string Body,
    string? ThreadId,
    string Status,
    DateTimeOffset CreatedAt,
    string? ProviderMessageId,
    string Kind = "Reply",
    string? SentByUserName = null,
    string SendingIdentityType = "Workspace",
    int Attempts = 0,
    string? LastError = null,
    DateTimeOffset? NextAttemptAtUtc = null,
    DateTimeOffset? ProviderAcceptedAtUtc = null);
public sealed record MarkOutboundSentRequest(string? ProviderMessageId);
