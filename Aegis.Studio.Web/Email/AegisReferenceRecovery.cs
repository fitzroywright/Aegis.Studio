using System.Text.RegularExpressions;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Email;

public static partial class AegisReferenceRecovery
{
    public static AegisReferenceRecoveryResult TryRecover(
        StudioDatabase database,
        StudioStore store,
        InboundEmail email,
        EmailRouteResult route)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(email);

        if (!route.IsRouted || !route.WorkspaceId.HasValue)
        {
            return AegisReferenceRecoveryResult.NoReference;
        }

        Match match = ReferenceRegex().Match(email.Subject);
        if (!match.Success)
        {
            return AegisReferenceRecoveryResult.NoReference;
        }

        string token = match.Groups[1].Value.ToUpperInvariant();
        List<Guid> candidates = [];

        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                select id
                from studio_conversations
                where workspace_id=$1
                  and lower(contact_address)=lower($2)
                  and not is_closed
                order by created_at_utc desc
                """;
            command.Parameters.AddWithValue(route.WorkspaceId.Value);
            command.Parameters.AddWithValue(email.FromAddress.Trim());
            using NpgsqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                Guid id = reader.GetGuid(0);
                if (id.ToString("N").StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(id);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return new AegisReferenceRecoveryResult(false, false, token, null);
        }

        if (candidates.Count > 1)
        {
            return new AegisReferenceRecoveryResult(false, true, token, null);
        }

        Guid conversationId = candidates[0];
        StudioConversation conversation = store.GetConversation(conversationId)
            ?? throw new InvalidOperationException("Referenced Studio conversation no longer exists.");

        using NpgsqlConnection writeConnection = database.OpenConnection();
        using NpgsqlTransaction transaction = writeConnection.BeginTransaction();

        using (NpgsqlCommand duplicateCommand = writeConnection.CreateCommand())
        {
            duplicateCommand.Transaction = transaction;
            duplicateCommand.CommandText = "select 1 from studio_messages where external_message_id=$1 limit 1";
            duplicateCommand.Parameters.AddWithValue(email.MessageId);
            if (duplicateCommand.ExecuteScalar() is not null)
            {
                transaction.Rollback();
                return new AegisReferenceRecoveryResult(
                    true,
                    false,
                    token,
                    new EmailIngestResult(conversation, false, true));
            }
        }

        using (NpgsqlCommand insertCommand = writeConnection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                insert into studio_messages
                (id, conversation_id, author_user_name, author_display_name, visibility, body, created_at_utc, external_message_id)
                values ($1,$2,$3,$4,'External',$5,$6,$7)
                """;
            insertCommand.Parameters.AddWithValue(Guid.NewGuid());
            insertCommand.Parameters.AddWithValue(conversationId);
            insertCommand.Parameters.AddWithValue(email.FromAddress.Trim());
            insertCommand.Parameters.AddWithValue(email.FromName.Trim());
            insertCommand.Parameters.AddWithValue(email.Body.Trim());
            insertCommand.Parameters.AddWithValue(email.ReceivedAt.ToUniversalTime());
            insertCommand.Parameters.AddWithValue(email.MessageId);
            insertCommand.ExecuteNonQuery();
        }

        if (!string.IsNullOrWhiteSpace(email.ThreadId))
        {
            using NpgsqlCommand bindThread = writeConnection.CreateCommand();
            bindThread.Transaction = transaction;
            bindThread.CommandText = "update studio_conversations set external_thread_id=coalesce(external_thread_id,$2) where id=$1";
            bindThread.Parameters.AddWithValue(conversationId);
            bindThread.Parameters.AddWithValue(email.ThreadId.Trim());
            bindThread.ExecuteNonQuery();
        }

        transaction.Commit();
        StudioConversation updated = store.GetConversation(conversationId)
            ?? throw new InvalidOperationException("Referenced Studio conversation disappeared after message ingestion.");
        return new AegisReferenceRecoveryResult(
            true,
            false,
            token,
            new EmailIngestResult(updated, false, false));
    }

    [GeneratedRegex(@"(?:^|[\s\[])AEGIS-([0-9A-Fa-f]{8})(?:\]|\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();
}

public sealed record AegisReferenceRecoveryResult(
    bool Matched,
    bool Ambiguous,
    string? ReferenceToken,
    EmailIngestResult? IngestResult)
{
    public static AegisReferenceRecoveryResult NoReference { get; } = new(false, false, null, null);
}
