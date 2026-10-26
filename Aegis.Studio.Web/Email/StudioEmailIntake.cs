using System.Security.Cryptography;
using System.Text.Json;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Storage;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Email;

public sealed record StudioEmailIntakeResult(bool Routed, Guid? ConversationId, Guid? ReviewId, bool WasDuplicate,Guid? WorkspaceId=null,bool CreatedConversation=false,string? FailureReason=null);
public sealed class StudioEmailIntake(StudioDatabase database, StudioEmailRouting routing, StudioStore studio,
    StudioUnifiedInbound unified, UnroutedEmailStore unresolved, AttachmentStore attachments,
    StudioInquiryAssociations associations,Aegis.Studio.Web.Notifications.NotificationService? notifications=null)
{
    public async Task<StudioEmailIntakeResult> ReceiveAsync(InboundEmail email, CancellationToken token,
        EmailRouteResult? fallback = null, EmailAssociation? confirmed = null, string? actor = null, bool confirmNewInquiry = false, System.Security.Claims.ClaimsPrincipal? user = null)
    {
        if(string.IsNullOrWhiteSpace(email.MessageId)||StudioEmailRouting.NormalizeAddress(email.FromAddress)=="")
            throw new InvalidOperationException("A stable message identifier and valid sender address are required.");
        email=email with {Subject=email.Subject??"(no subject)",Body=email.Body??"",FromName=string.IsNullOrWhiteSpace(email.FromName)?email.FromAddress:email.FromName};
        var normalizedIds=StudioEmailRouting.MessageIds(email.MessageId);
        if(normalizedIds.Length==1)email=email with {MessageId=normalizedIds[0]};
        // Serialize all entry points on the same Internet Message-ID, including attachment retries.
        using var c=database.OpenConnection();
        try
        {
        var initial=routing.Resolve(email);
        if(initial.ReviewReason?.StartsWith("Provider and Internet Message-ID",StringComparison.Ordinal)==true)return Hold(email,initial,initial.Workspace);
        if(!string.IsNullOrWhiteSpace(email.ProviderMessageId))
        {
            SessionLock(c,"email-provider:"+email.ProviderMessageId);
            using var canonical=new NpgsqlCommand("select external_message_id from studio_email_intake_receipts where provider_message_id=$1",c);Add(canonical,email.ProviderMessageId);
            if(canonical.ExecuteScalar() is string external)email=email with {MessageId=external};
        }
        using(var legacyId=new NpgsqlCommand("select external_message_id,conversation_id from studio_messages where trim(both '<>' from external_message_id)=$1",c))
        {
            Add(legacyId,email.MessageId.Trim('<','>'));using var r=legacyId.ExecuteReader();List<(string External,Guid Conversation)> old=[];
            while(r.Read())old.Add((r.GetString(0),r.GetGuid(1)));r.Close();
            if(old.Count>1)return Hold(email,initial with {Association=null,ReviewReason="Ambiguous historical message identifiers—staff review required",Candidates=old.Select(x=>new EmailAssociation(x.Conversation,null)).Distinct().ToArray()},initial.Workspace);
            if(old.Count==1)email=email with {MessageId=old[0].External};
        }
        SessionLock(c,"email-intake:"+email.MessageId);
        string fingerprint=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            sender=StudioEmailRouting.NormalizeAddress(email.FromAddress),email.Subject,email.Body,
            files=email.FileAttachments.Select(f=>new{f.FileName,f.ContentType,hash=Convert.ToHexString(SHA256.HashData(f.Content))}).OrderBy(f=>f.FileName).ThenBy(f=>f.hash)
        }))));
        using(var own=new NpgsqlCommand("select exists(select 1 from studio_outbound_email where rfc_message_id=$1 and studio_normalize_email(from_address)=$2 and rfc_message_id_verified)",c))
        {Add(own,email.MessageId,StudioEmailRouting.NormalizeAddress(email.FromAddress));if((bool)own.ExecuteScalar()!){return new(true,null,null,true);}}
        var decision=routing.Resolve(email);
        using(var original=new NpgsqlCommand("select conversation_id,body,coalesce(author_user_name,'') from studio_messages where external_message_id=$1",c))
        {
            Add(original,email.MessageId);using var r=original.ExecuteReader();
            if(r.Read() && (r.GetString(1)!=email.Body.Trim() || StudioEmailRouting.NormalizeAddress(r.GetString(2))!=StudioEmailRouting.NormalizeAddress(email.FromAddress)))
            {
                Guid originalId=r.GetGuid(0);r.Close();return Hold(email,decision with {Association=null,ReviewReason="Duplicate message identifier has different content—staff review required",Candidates=[new(originalId,null)]},decision.Workspace);
            }
        }
        using(var payload=new NpgsqlCommand("select payload_hash from studio_email_intake_receipts where external_message_id=$1",c))
        {
            Add(payload,email.MessageId);if(payload.ExecuteScalar() is string priorHash && priorHash!=fingerprint)
                return Hold(email,decision with {Association=null,ReviewReason="Duplicate message identifier has different content—staff review required"},decision.Workspace);
        }
        bool duplicate=false;
        using(var prior=new NpgsqlCommand("select exists(select 1 from studio_email_intake_receipts where external_message_id=$1 or ($2::text is not null and provider_message_id=$2))",c))
        {Add(prior,email.MessageId,email.ProviderMessageId);duplicate=(bool)prior.ExecuteScalar()!;}
        // Confirmation never overrides an existing staff correction or moves an already imported message.
        var target=duplicate?decision.Association:confirmNewInquiry?null:confirmed??decision.Association;
        var workspace=confirmNewInquiry && fallback is not null ? fallback : decision.Workspace.IsRouted?decision.Workspace:fallback??decision.Workspace;
        if(!duplicate && confirmed is null && !confirmNewInquiry && decision.ReviewReason is not null)
            return Hold(email,decision,workspace);
        if(target?.ThreadId is Guid thread)
        {
            if(user is not null && !associations.CanReviewThread(thread,user))throw new UnauthorizedAccessException();
            await unified.TryImportAsync(email,token,thread);
            using var threadTx=c.BeginTransaction();
            SaveReceipt(c,threadTx,email,null,thread,null,true,confirmed is not null || confirmNewInquiry,fingerprint);
            threadTx.Commit();return new(true,null,null,duplicate);
        }
        if(target?.ConversationId is Guid conversationId)
        {
            if(user is not null)associations.AssertAccess(conversationId,user,true);
            var conversation=studio.GetConversation(conversationId)??throw new KeyNotFoundException();
            workspace=new(true,conversation.WorkspaceId,null,null,null);
        }
        if(!workspace.IsRouted||!workspace.WorkspaceId.HasValue)
        {
            // Personal Studio inboxes remain supported; workspace destinations use inquiries.
            if(decision.ReviewReason is null && await unified.TryImportAsync(email,token))
            {return new(true,null,null,duplicate);}
            // A new inquiry without a matching destination belongs in the permanent
            // Unassigned workspace. Do not override ambiguous workspace matches,
            // conflicting references, or other explicit review holds.
            if(!duplicate && decision.ReviewReason is null && target is null && fallback is null &&
                workspace.FailureReason?.StartsWith("No persisted workspace route matched",StringComparison.Ordinal)==true)
            {
                using var unassigned=new NpgsqlCommand("select id,name from studio_workspaces where system_key='unassigned' and is_enabled",c);
                using var reader=unassigned.ExecuteReader();
                if(reader.Read())workspace=new(true,reader.GetGuid(0),reader.GetString(1),null,null);
                reader.Close();
            }
            if(!workspace.IsRouted||!workspace.WorkspaceId.HasValue)
                return Hold(email,decision with {ReviewReason=workspace.FailureReason??"No workspace route resolved"},workspace);
        }
        if(user is not null && target?.ConversationId is null)associations.AssertWorkspace(workspace.WorkspaceId.Value,user);
        var ingest=studio.ReceiveEmail(email,workspace,target?.ConversationId);
        Guid messageId;
        using(var message=new NpgsqlCommand("select id from studio_messages where external_message_id=$1",c))
        {Add(message,email.MessageId);messageId=(Guid)message.ExecuteScalar()!;}
        if(ingest.CreatedConversation)associations.MatchClient(ingest.Conversation.Id,email.FromAddress);
        using(var pendingTx=c.BeginTransaction())
        {
            SaveReceipt(c,pendingTx,email,ingest.Conversation.Id,null,messageId,false,confirmed is not null || confirmNewInquiry,fingerprint);
            if((confirmed is not null || confirmNewInquiry) && !duplicate)
                StudioInquiryAssociations.History(c,pendingTx,ingest.Conversation.Id,email.MessageId,"Confirm email association",actor??"Staff",decision.Candidates,new EmailAssociation(ingest.Conversation.Id,null));
            pendingTx.Commit();
        }
        foreach(var attachment in email.FileAttachments)
        {
            string hash=Convert.ToHexString(SHA256.HashData(attachment.Content));
            string key=$"graph:{email.MessageId}:{attachment.FileName}:{hash}";
            var stored=await attachments.AddBytesAsync(ingest.Conversation.Id,attachment.FileName,attachment.ContentType,
                attachment.Content,"External",email.FromAddress,token,key);
            using var link=new NpgsqlCommand("insert into studio_email_attachment_links(message_id,attachment_id) values($1,$2) on conflict do nothing",c);
            Add(link,messageId,stored.Id);link.ExecuteNonQuery();
        }
        using var receiptTx=c.BeginTransaction();
        SaveReceipt(c,receiptTx,email,ingest.Conversation.Id,null,messageId,true,confirmed is not null || confirmNewInquiry,fingerprint);
        receiptTx.Commit();
        if(!ingest.WasDuplicate)notifications?.PublishToAll("WorkspaceMessage",$"Email: {email.Subject}",
            $"New email from {email.FromName}.","Conversation",ingest.Conversation.Id.ToString("D"));
        return new(true,ingest.Conversation.Id,null,ingest.WasDuplicate,ingest.Conversation.WorkspaceId,ingest.CreatedConversation);
        }
        finally
        {
            // Dedicated session locks span the existing durable message and attachment stores.
            // Always release before this connection returns to the pool, including failed uploads.
            using var unlock=new NpgsqlCommand("select pg_advisory_unlock_all()",c);unlock.ExecuteNonQuery();
        }
    }
    private static void SessionLock(NpgsqlConnection c,string key)
    {using var q=new NpgsqlCommand("select pg_advisory_lock(hashtextextended($1,0))",c);Add(q,key);q.ExecuteNonQuery();}
    private StudioEmailIntakeResult Hold(InboundEmail email,EmailRoutingDecision decision,EmailRouteResult workspace)
    {
        var item=unresolved.Capture(email,decision.ReviewReason??"Workspace routing unresolved");
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("update studio_unrouted_email set failure_reason=$2,candidate_conversations=$3,candidate_threads=$4,routing_workspace_id=$5 where id=$1 and resolved_at_utc is null",c);
        Add(q,item.Id,decision.ReviewReason??workspace.FailureReason??"Workspace routing unresolved",
            decision.Candidates.Where(x=>x.ConversationId.HasValue).Select(x=>x.ConversationId!.Value).ToArray(),
            decision.Candidates.Where(x=>x.ThreadId.HasValue).Select(x=>x.ThreadId!.Value).ToArray(),workspace.WorkspaceId);q.ExecuteNonQuery();
        return new(false,null,item.Id,false,workspace.WorkspaceId,false,decision.ReviewReason??workspace.FailureReason);
    }
    private static void SaveReceipt(NpgsqlConnection c,NpgsqlTransaction tx,InboundEmail email,Guid? conversation,Guid? thread,Guid? message,bool complete,bool corrected,string fingerprint)
    {
        using var q=new NpgsqlCommand("""
            insert into studio_email_intake_receipts(external_message_id,provider_message_id,conversation_id,thread_id,message_id,internet_headers,received_at_utc,attachments_complete,staff_corrected,payload_hash)
            values($1,$2,$3,$4,$5,$6::jsonb,$7,$8,$9,$10)
            on conflict(external_message_id) do update set attachments_complete=studio_email_intake_receipts.attachments_complete or excluded.attachments_complete
            """,c,tx);Add(q,email.MessageId,email.ProviderMessageId,conversation,thread,message,JsonSerializer.Serialize(email.InternetHeaders),email.ReceivedAt,complete,corrected,fingerprint);q.ExecuteNonQuery();
    }
}
