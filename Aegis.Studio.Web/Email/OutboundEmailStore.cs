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

    public OutboundEmail QueueIdempotent(
        StudioConversation conversation,
        string body,
        StudioSendingIdentity? sendingIdentity,
        Guid deliveryId)
        => QueueMessage(
            conversation,
            body,
            conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : "Re: " + conversation.Subject,
            sendingIdentity: sendingIdentity,
            idempotencyId: deliveryId);

    public OutboundEmail QueueNotification(StudioConversation conversation, string subject, string body)
        => QueueMessage(conversation, body, subject, "Notification");

    public OutboundEmail? QueueAcknowledgement(StudioConversation conversation, string workspaceName)
    {
        if (ExistsAcknowledgement(conversation.Id)) return null;
        using var referenceConnection=database.OpenConnection();
        using var referenceCommand=new NpgsqlCommand("select intake_reference from studio_conversations where id=$1",referenceConnection);
        referenceCommand.Parameters.AddWithValue(conversation.Id);
        string jobReference = referenceCommand.ExecuteScalar() as string ?? StudioEmailRouting.PublicReference(conversation.Id);
        string body = $"Hello {conversation.ContactName},\n\nWe received your message and it is saved for Front Desk review. Our team will contact you with next steps. Receipt does not confirm availability, payment or booking.\n\nReference: {jobReference}\nPlease keep this reference in the subject when starting a separate email about the same request.\n\nBryan's Studio";
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

    public OutboundEmail? ClaimNext(IReadOnlyCollection<string> channels,Guid leaseId)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using(var exhausted=new NpgsqlCommand("update studio_outbound_email set status='Attention',last_error='ProviderOutcomeUncertainAfterLeaseExpiry',lease_id=null,lease_until_utc=null where status='Sending' and lease_until_utc<now()",c,tx))exhausted.ExecuteNonQuery();
        using var q=new NpgsqlCommand("""
            with candidate as (select id from studio_outbound_email
              where delivery_channel=any($1) and attempts<10 and
                (kind<>'ClientReminder' or (studio_client_reminder_dispatchable(id) and extract(hour from now() at time zone coalesce(delivery_metadata->>'ReminderTimeZone','America/Jamaica')) between 9 and 17)) and
                ((status in ('Pending','Retry') and (next_attempt_at_utc is null or next_attempt_at_utc<=now()))
                  or false)
              order by created_at_utc for update skip locked limit 1)
            update studio_outbound_email e set status='Sending',attempts=attempts+1,lease_id=$2,lease_until_utc=now()+interval '2 minutes'
              from candidate p where e.id=p.id returning e.id
            """,c,tx);q.Parameters.AddWithValue(channels.ToArray());q.Parameters.AddWithValue(leaseId);
        object? result=q.ExecuteScalar();tx.Commit();return result is Guid id?Get(id):null;
    }

    public IReadOnlyList<OutboundEmailAuditRecord> GetRecentAudit(
        IReadOnlyCollection<Guid> workspaceIds,
        int count = 200)
    {
        if (workspaceIds.Count == 0) return [];

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select e.id,e.conversation_id,e.from_address,e.to_address,e.status,e.created_at_utc,
                   e.provider_message_id,e.kind,e.sent_by_user_name,e.sending_identity_type,c.workspace_id,e.delivery_channel
            from studio_outbound_email e
            join studio_conversations c on c.id=e.conversation_id
            where c.workspace_id = any($1)
            order by e.created_at_utc desc
            limit $2
            """;
        command.Parameters.AddWithValue(workspaceIds.ToArray());
        command.Parameters.AddWithValue(Math.Clamp(count, 1, 1000));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<OutboundEmailAuditRecord> values = [];
        while (reader.Read())
        {
            values.Add(new(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetGuid(10),reader.GetString(11)));
        }
        return values;
    }

    public OutboundEmail? MarkProviderAccepted(Guid id, string? providerMessageId,Guid? leaseId=null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_outbound_email
            set status='ProviderAccepted',
                provider_message_id=coalesce($2,provider_message_id),
                attempts=attempts+case when $3::uuid is null then 1 else 0 end,
                last_error=null,
                next_attempt_at_utc=null,
                provider_accepted_at_utc=coalesce(provider_accepted_at_utc,now()),lease_id=null,lease_until_utc=null
            where id=$1 and ($3::uuid is null or lease_id=$3)
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue((object?)providerMessageId?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)leaseId??DBNull.Value);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public OutboundEmail? MarkFailure(Guid id, string failureCode, DateTimeOffset nextAttemptAtUtc,Guid? leaseId=null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_outbound_email
            set attempts=attempts+case when $4::uuid is null then 1 else 0 end,
                status=case when attempts+case when $4::uuid is null then 1 else 0 end >= 10 then 'Failed' else 'Retry' end,
                last_error=$2,
                next_attempt_at_utc=case when attempts+case when $4::uuid is null then 1 else 0 end >= 10 then null else $3 end,lease_id=null,lease_until_utc=null
            where id=$1 and provider_accepted_at_utc is null and ($4::uuid is null or lease_id=$4)
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(string.IsNullOrWhiteSpace(failureCode) ? "ProviderFailure" : failureCode.Trim());
        command.Parameters.AddWithValue(nextAttemptAtUtc);
        command.Parameters.AddWithValue((object?)leaseId??DBNull.Value);
        return command.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public void SetRfcMessageId(Guid id,Guid leaseId,string rfcId)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("update studio_outbound_email set rfc_message_id=$3 where id=$1 and lease_id=$2",c);
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(leaseId);q.Parameters.AddWithValue(rfcId);
        if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("The delivery lease was lost before sending.");
    }

    public void RecordVerifiedInternetId(Guid id,Guid? leaseId,VerifiedGraphEmail verified)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var q=new NpgsqlCommand("update studio_outbound_email set rfc_message_id=$3,rfc_message_id_verified=true,provider_message_id=$4,status=case when $2::uuid is null then 'ProviderAccepted' else status end,last_error=case when $2::uuid is null then null else last_error end,provider_accepted_at_utc=case when $2::uuid is null then coalesce(provider_accepted_at_utc,now()) else provider_accepted_at_utc end where id=$1 and (($2::uuid is not null and lease_id=$2) or ($2::uuid is null and status in ('ProviderAccepted','Attention')))",c,tx);
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue((object?)leaseId??DBNull.Value);q.Parameters.AddWithValue(verified.InternetMessageId);q.Parameters.AddWithValue(verified.ProviderId);
        if(q.ExecuteNonQuery()!=1)throw new InvalidOperationException("Delivery lease expired during provider verification.");
        using var history=new NpgsqlCommand("insert into studio_outbound_message_ids(delivery_id,internet_message_id) values($1,$2) on conflict do nothing",c,tx);
        history.Parameters.AddWithValue(id);history.Parameters.AddWithValue(verified.InternetMessageId);history.ExecuteNonQuery();tx.Commit();
    }

    public void MarkAttention(Guid id, Guid leaseId, string reason,bool providerAccepted=false)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("update studio_outbound_email set status='Attention',last_error=$3,provider_accepted_at_utc=case when $4 then now() else provider_accepted_at_utc end,next_attempt_at_utc=null,lease_id=null,lease_until_utc=null where id=$1 and lease_id=$2",c);
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(leaseId);q.Parameters.AddWithValue(reason);q.Parameters.AddWithValue(providerAccepted);q.ExecuteNonQuery();
    }

    public void MarkVerificationFailure(Guid id,string failure)
    {
        using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("update studio_outbound_email set last_error=$2 where id=$1 and provider_accepted_at_utc is not null and status='ProviderAccepted'",c);
        q.Parameters.AddWithValue(id);q.Parameters.AddWithValue("ThreadingVerificationPending:"+failure);q.ExecuteNonQuery();
    }

    public OutboundEmail? GetDelivery(Guid id)=>Get(id);

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
        StudioSendingIdentity? sendingIdentity = null,
        Guid? idempotencyId = null)
    {
        StudioWorkspaceDefinition workspace = workspaces.Get(conversation.WorkspaceId) ?? throw new InvalidOperationException("The conversation workspace no longer exists.");
        if (string.IsNullOrWhiteSpace(workspace.EmailAddress)) throw new InvalidOperationException("The conversation workspace has no outbound email identity configured.");

        StudioSendingIdentity identity = sendingIdentity ??
            new StudioSendingIdentity(StudioSendingIdentityType.Workspace, workspace.EmailAddress, null);

        OutboundEmail message = new(
            idempotencyId ?? Guid.NewGuid(),
            conversation.Id,
            identity.FromAddress,
            conversation.ContactAddress,
            $"[{StudioEmailRouting.PublicReference(conversation.Id)}] " + System.Text.RegularExpressions.Regex.Replace(subject,@"\[AEGIS-[A-Za-z0-9]+\]\s*", ""),
            body.Trim()+$"\n\nFor a new email about this request, include {StudioEmailRouting.PublicReference(conversation.Id)} in the subject.",
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
        var metadata=new Dictionary<string,string>();
        using(var reply=new NpgsqlCommand("select external_message_id from studio_messages where conversation_id=$1 and external_message_id is not null order by created_at_utc desc limit 1",connection))
        {
            reply.Parameters.AddWithValue(conversation.Id);
            if(reply.ExecuteScalar() is string previous && StudioEmailRouting.MessageIds(previous).FirstOrDefault() is string reference)
            {metadata["Email.InReplyTo"]=reference;metadata["Email.References"]=reference;}
        }
        message=message with {DeliveryMetadata=System.Text.Json.JsonSerializer.Serialize(metadata)};
        command.CommandText = """
            insert into studio_outbound_email
            (id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind,sent_by_user_name,sending_identity_type,delivery_metadata)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14::jsonb)
            on conflict (id) do nothing
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
            message.SendingIdentityType,
            message.DeliveryMetadata
        ];
        foreach (object? value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        int inserted = command.ExecuteNonQuery();
        return inserted == 0 ? Get(message.Id) ?? throw new InvalidOperationException("The idempotent outbound email was not found.") : message;
    }

    private static OutboundEmail Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.IsDBNull(1) ? r.GetGuid(19) : r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetFieldValue<DateTimeOffset>(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.GetString(12),
        r.GetInt32(13),
        r.IsDBNull(14) ? null : r.GetString(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetFieldValue<DateTimeOffset>(16),r.GetString(17),r.GetString(18),r.IsDBNull(20)?null:r.GetGuid(20),r.IsDBNull(21)?null:r.GetString(21),r.IsDBNull(22)?null:r.GetString(22),r.GetBoolean(23));

    private const string SelectSql =
        "select id,conversation_id,from_address,to_address,subject,body,thread_id,status,created_at_utc,provider_message_id,kind,sent_by_user_name,sending_identity_type,attempts,last_error,next_attempt_at_utc,provider_accepted_at_utc,delivery_channel,delivery_metadata::text,studio_thread_id,studio_message_id,rfc_message_id,sender_mailbox,rfc_message_id_verified from studio_outbound_email ";
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
    DateTimeOffset? ProviderAcceptedAtUtc = null,string DeliveryChannel="Email",string DeliveryMetadata="{}",Guid? StudioMessageId=null,string? RfcMessageId=null,string? SenderMailbox=null,bool RfcMessageIdVerified=false)
{
    public string ThreadingVerificationStatus=>DeliveryChannel!="Email"?"NotApplicable":RfcMessageIdVerified?"Verified":ProviderAcceptedAtUtc.HasValue?"Pending":"NotSubmitted";
}
public sealed record OutboundEmailAuditRecord(
    Guid MessageId,
    Guid ConversationId,
    string FromAddress,
    string ToAddress,
    string Status,
    DateTimeOffset CreatedAtUtc,
    string? ProviderMessageId,
    string Kind,
    string? ActualUser,
    string SendingIdentityType,
    Guid WorkspaceId,string DeliveryChannel="Email");

public sealed record MarkOutboundSentRequest(string? ProviderMessageId);



