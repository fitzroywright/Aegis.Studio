using System.Text.Json;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Aegis.Studio.Web.Email;

public sealed class UnroutedEmailStore
{
    private readonly StudioDatabase database;

    public UnroutedEmailStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<UnroutedEmail> GetOpen()
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where resolved_at_utc is null order by received_at_utc desc";using NpgsqlDataReader reader=command.ExecuteReader();List<UnroutedEmail> values=[];while(reader.Read())values.Add(Read(reader));return values;
    }

    public UnroutedEmail Capture(InboundEmail email, string reason)
    {
        UnroutedEmail? existing=FindByProviderMessageId(email.MessageId);if(existing is not null)return existing;
        UnroutedEmail item=new(Guid.NewGuid(),email.MessageId,email.ThreadId,email.FromName,email.FromAddress,email.ToAddresses.ToList(),email.CcAddresses.ToList(),new Dictionary<string,string>(email.InternetHeaders,StringComparer.OrdinalIgnoreCase),email.Subject,email.Body,email.ReceivedAt.ToUniversalTime(),reason,null,null,null,email.FileAttachments);
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="insert into studio_unrouted_email(id,provider_message_id,thread_id,from_name,from_address,to_addresses,cc_addresses,internet_headers,subject,body,received_at_utc,failure_reason,resolved_at_utc,resolved_workspace_id,resolved_workspace_name,file_attachments,source_provider_message_id) values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,null,null,null,$13,$14) on conflict(provider_message_id) do nothing";command.Parameters.AddWithValue(item.Id);command.Parameters.AddWithValue(item.ProviderMessageId);command.Parameters.AddWithValue((object?)item.ThreadId??DBNull.Value);command.Parameters.AddWithValue(item.FromName);command.Parameters.AddWithValue(item.FromAddress);command.Parameters.AddWithValue(item.ToAddresses.ToArray());command.Parameters.AddWithValue(item.CcAddresses.ToArray());command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(item.InternetHeaders));command.Parameters.AddWithValue(item.Subject);command.Parameters.AddWithValue(item.Body);command.Parameters.AddWithValue(item.ReceivedAt);command.Parameters.AddWithValue(item.FailureReason);command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,JsonSerializer.Serialize(email.FileAttachments));command.Parameters.AddWithValue((object?)email.ProviderMessageId??DBNull.Value);command.ExecuteNonQuery();return FindByProviderMessageId(email.MessageId)!;
    }

    public UnroutedEmail? Get(Guid id)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where id=$1";command.Parameters.AddWithValue(id);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    public UnroutedEmail? MarkResolved(Guid id, Guid workspaceId, string workspaceName)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText="update studio_unrouted_email set resolved_at_utc=$2,resolved_workspace_id=$3,resolved_workspace_name=$4 where id=$1";command.Parameters.AddWithValue(id);command.Parameters.AddWithValue(DateTimeOffset.UtcNow);command.Parameters.AddWithValue(workspaceId);command.Parameters.AddWithValue(workspaceName);return command.ExecuteNonQuery()==0?null:Get(id);
    }

    private UnroutedEmail? FindByProviderMessageId(string providerMessageId)
    {
        using NpgsqlConnection connection=database.OpenConnection();using NpgsqlCommand command=connection.CreateCommand();command.CommandText=SelectSql+" where provider_message_id=$1";command.Parameters.AddWithValue(providerMessageId);using NpgsqlDataReader reader=command.ExecuteReader();return reader.Read()?Read(reader):null;
    }

    private static UnroutedEmail Read(NpgsqlDataReader r)
    {
        Dictionary<string,string> headers=JsonSerializer.Deserialize<Dictionary<string,string>>(r.GetString(7))??new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        return new UnroutedEmail(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.GetString(3),r.GetString(4),r.GetFieldValue<string[]>(5).ToList(),r.GetFieldValue<string[]>(6).ToList(),headers,r.GetString(8),r.GetString(9),r.GetFieldValue<DateTimeOffset>(10),r.GetString(11),r.IsDBNull(12)?null:r.GetFieldValue<DateTimeOffset>(12),r.IsDBNull(13)?null:r.GetGuid(13),r.IsDBNull(14)?null:r.GetString(14),JsonSerializer.Deserialize<List<InboundEmailAttachment>>(r.GetString(15))??[],r.GetFieldValue<Guid[]>(16),r.GetFieldValue<Guid[]>(17),r.IsDBNull(18)?null:r.GetGuid(18),r.IsDBNull(19)?null:r.GetString(19));
    }

    private const string SelectSql="select id,provider_message_id,thread_id,from_name,from_address,to_addresses,cc_addresses,internet_headers::text,subject,body,received_at_utc,failure_reason,resolved_at_utc,resolved_workspace_id,resolved_workspace_name,file_attachments::text,candidate_conversations,candidate_threads,routing_workspace_id,source_provider_message_id from studio_unrouted_email";
}

public sealed record UnroutedEmail(Guid Id,string ProviderMessageId,string? ThreadId,string FromName,string FromAddress,List<string> ToAddresses,List<string> CcAddresses,Dictionary<string,string> InternetHeaders,string Subject,string Body,DateTimeOffset ReceivedAt,string FailureReason,DateTimeOffset? ResolvedAt,Guid? ResolvedWorkspaceId,string? ResolvedWorkspaceName,IReadOnlyCollection<InboundEmailAttachment>? FileAttachments=null,Guid[]? CandidateConversations=null,Guid[]? CandidateThreads=null,Guid? RoutingWorkspaceId=null,string? SourceProviderMessageId=null)
{
    public InboundEmail ToInboundEmail()=>new(ProviderMessageId,ThreadId,FromName,FromAddress,ToAddresses,CcAddresses,InternetHeaders,Subject,Body,ReceivedAt,FileAttachments,SourceProviderMessageId);
}
public sealed record ResolveUnroutedEmailRequest(Guid WorkspaceId);

