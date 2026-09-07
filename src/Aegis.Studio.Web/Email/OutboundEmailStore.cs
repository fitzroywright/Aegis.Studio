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

    public OutboundEmail Queue(StudioConversation conversation, string body)
        => QueueMessage(conversation, body, conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : "Re: " + conversation.Subject);

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
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where status='Pending' order by created_at_utc";using NpgsqlDataReader reader=command.ExecuteReader();List<OutboundEmail> values=[];while(reader.Read())values.Add(Read(reader));return values;
    }

    public OutboundEmail? MarkSent(Guid id, string? providerMessageId)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="update studio_outbound_email set status='Sent',provider_message_id=$2 where id=$1";command.Parameters.AddWithValue(id);command.Parameters.AddWithValue((object?)providerMessageId?.Trim()??DBNull.Value);return command.ExecuteNonQuery()==0?null:Get(id);
    }

    private OutboundEmail? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    private bool ExistsAcknowledgement(Guid conversationId)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="select exists(select 1 from studio_outbound_email where conversation_id=$1 and lower(kind)='acknowledgement')";command.Parameters.AddWithValue(conversationId);return (bool)(command.ExecuteScalar()??false);
    }

    private OutboundEmail QueueMessage(StudioConversation conversation, string body, string subject, string kind = "Reply")
    {
        StudioWorkspaceDefinition workspace = workspaces.Get(conversation.WorkspaceId) ?? throw new InvalidOperationException("The conversation workspace no longer exists.");
        if (string.IsNullOrWhiteSpace(workspace.EmailAddress)) throw new InvalidOperationException("The conversation workspace has no outbound email identity configured.");
        OutboundEmail message=new(Guid.NewGuid(),conversation.Id,workspace.EmailAddress,conversation.ContactAddress,subject,body.Trim(),conversation.ExternalThreadId,"Pending",DateTimeOffset.UtcNow,null,kind);
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="insert into studio_outbound_email(id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)";object?[] values=[message.Id,message.ConversationId,message.FromAddress,message.ToAddress,message.Subject,message.Body,(object?)message.ThreadId??DBNull.Value,message.Status,message.CreatedAt,(object?)message.ProviderMessageId??DBNull.Value,message.Kind];foreach(object? value in values)command.Parameters.AddWithValue(value??DBNull.Value);command.ExecuteNonQuery();return message;
    }

    private static OutboundEmail Read(NpgsqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetString(7),r.GetFieldValue<DateTimeOffset>(8),r.IsDBNull(9)?null:r.GetString(9),r.GetString(10));
    private const string SelectSql="select id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind from studio_outbound_email";
}

public sealed record OutboundEmail(Guid Id,Guid ConversationId,string FromAddress,string ToAddress,string Subject,string Body,string? ThreadId,string Status,DateTimeOffset CreatedAt,string? ProviderMessageId,string Kind="Reply");
public sealed record MarkOutboundSentRequest(string? ProviderMessageId);
