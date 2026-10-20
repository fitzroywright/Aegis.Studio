using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using static Aegis.Studio.Web.Messaging.StudioUnifiedMessaging;

namespace Aegis.Studio.Web.Messaging;

public sealed class StudioUnifiedInbound(StudioDatabase database, StudioAddressBook addresses, StudioMessageAttachmentStore attachments,
    IConfiguration configuration, IHubContext<StudioMessageHub> hub, StudioInAppMessageStore messages)
{
    private string ProviderIdentity => "Microsoft365:" + StudioAddressBook.Normalize(configuration["Messaging:MicrosoftGraph:Email:SenderUpn"]);

    public async Task<bool> TryImportAsync(InboundEmail email, CancellationToken token, Guid? associatedThread = null)
    {
        if (IsOwnOutbound(email)) return true;
        Guid? replyThread = associatedThread;
        List<(Guid ThreadId, string DestinationKey)> targets = [];
        if (replyThread.HasValue) targets.Add((replyThread.Value, "reply:" + replyThread.Value.ToString("D")));
        else
        {
            var resolved = email.ToAddresses.Concat(email.CcAddresses).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(addresses.Resolve).Where(e => e is not null).Cast<StudioAddressEntry>()
                .Where(e => e.ContactType is not "Workspace" and not "Job" && e.DeliveryMode != "ExternalOnly" && (e.InboxType == "Microsoft365" || e.StudioManagedGroup)).DistinctBy(e => e.Id).ToArray();
            if (resolved.Length == 0) return false;
            foreach (var destination in resolved)
            {
                Guid id = FindInboundThread(destination, email);
                targets.Add((id, destination.Id.ToString("D")));
            }
        }
        // A receipt per destination protects multi-address routing and attachments across repeated delta batches.
        foreach (var target in targets.DistinctBy(t => t.ThreadId))
        {
            Guid messageId; bool complete;
            using (var c = database.OpenConnection())
            using (var tx = c.BeginTransaction())
            {
                LockRequest(c, tx, ProviderIdentity + email.MessageId + target.DestinationKey);
                using var prior = new NpgsqlCommand("select message_id,status from studio_inbound_receipts where provider_identity=$1 and external_message_id=$2 and destination_key=$3", c, tx);
                Add(prior, ProviderIdentity, email.MessageId, target.DestinationKey); using var r = prior.ExecuteReader();
                if (r.Read()) { messageId = r.GetGuid(0); complete = r.GetString(1) == "Complete"; r.Close(); }
                else
                {
                    r.Close(); complete = false; messageId = Guid.NewGuid();
                    using var insert = new NpgsqlCommand("insert into studio_inapp_messages(id,thread_id,sender_user_name,body,created_at_utc,subject,recipient_addresses,internet_message_id,internet_headers) values($1,$2,$3,$4,$5,$6,$7,$8,$9::jsonb)", c, tx);
                    Add(insert, messageId, target.ThreadId, email.FromAddress, email.Body, email.ReceivedAt.ToUniversalTime(), email.Subject,
                        email.ToAddresses.Concat(email.CcAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), email.MessageId, JsonSerializer.Serialize(email.InternetHeaders)); insert.ExecuteNonQuery();
                    var replyContact=addresses.GetOrCreateExternal(email.FromName,email.FromAddress);
                    using(var recipient=new NpgsqlCommand("insert into studio_message_route_recipients(message_id,destination_id) values($1,$2) on conflict do nothing",c,tx)){Add(recipient,messageId,replyContact.Id);recipient.ExecuteNonQuery();}
                    using var receipt = new NpgsqlCommand("insert into studio_inbound_receipts(provider_identity,external_message_id,destination_key,message_id,status) values($1,$2,$3,$4,'AttachmentsPending')", c, tx);
                    Add(receipt, ProviderIdentity, email.MessageId, target.DestinationKey, messageId); receipt.ExecuteNonQuery();
                    Ledger(c, tx, messageId, target.DestinationKey, "Internal", "Delivered", null, "Imported from monitored Microsoft 365 mailbox.");
                }
                tx.Commit();
            }
            if (complete) continue;
            foreach (var file in email.FileAttachments)
            {
                using var stream = new MemoryStream(file.Content);
                var form = new FormFile(stream, 0, file.Content.Length, "file", file.FileName) { Headers = new HeaderDictionary(), ContentType = file.ContentType };
                string hash = Convert.ToHexString(SHA256.HashData(file.Content));
                await attachments.AddAsync(messageId, form, email.FromAddress, token, $"inbound:{ProviderIdentity}:{email.MessageId}:{target.DestinationKey}:{file.FileName}:{hash}");
            }
            using (var c = database.OpenConnection())
            using (var done = new NpgsqlCommand("update studio_inbound_receipts set status='Complete' where provider_identity=$1 and external_message_id=$2 and destination_key=$3", c))
            { Add(done, ProviderIdentity, email.MessageId, target.DestinationKey); done.ExecuteNonQuery(); }
            await hub.Clients.Group(StudioMessageHub.ThreadGroup(target.ThreadId)).SendAsync("threadChanged", target.ThreadId, token);
            foreach(var participant in messages.GetThreadParticipants(target.ThreadId))
                await hub.Clients.Group(StudioMessageHub.UserGroup(participant)).SendAsync("threadChanged",target.ThreadId,token);
        }
        return true;
    }

    public static string[] ReplyReferences(IReadOnlyDictionary<string, string> headers)
        => headers.Where(h => h.Key.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("References", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => Regex.Matches(h.Value, @"<[^<>\s]+@[^<>\s]+>").Select(m => m.Value)).Distinct(StringComparer.Ordinal).ToArray();

    private bool IsOwnOutbound(InboundEmail email)
    {
        using var c = database.OpenConnection(); using var q = new NpgsqlCommand("select exists(select 1 from studio_outbound_email where rfc_message_id=$1 and lower(from_address)=lower($2))", c);
        Add(q, email.MessageId, email.FromAddress.Trim()); return (bool)q.ExecuteScalar()!;
    }

    private Guid FindInboundThread(StudioAddressEntry destination, InboundEmail email)
    {
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        // Keep messages from the same provider conversation together in this destination's private inbox.
        // Fall back to the message ID when the provider has no thread identifier.
        string conversationKey = string.IsNullOrWhiteSpace(email.ThreadId)
            ? email.MessageId.Trim()
            : "thread:" + email.ThreadId.Trim();
        string key = $"inbound:{destination.Id:D}:{conversationKey}";
        LockRequest(c, tx, key);
        string scope = destination.ContactType is "Workspace" or "Job" ? destination.ContactType : "Direct";
        using var find = new NpgsqlCommand(scope == "Direct" ? "select id from studio_message_threads where direct_key=$1" : "select id from studio_message_threads where scope_type=$1 and scope_id=$2", c, tx);
        if (scope == "Direct") Add(find, key); else Add(find, scope, destination.ScopeId);
        if (find.ExecuteScalar() is Guid existing) { tx.Commit(); return existing; }
        Guid id = Guid.NewGuid();
        using var insert = new NpgsqlCommand("insert into studio_message_threads(id,scope_type,scope_id,direct_key,title,created_by,created_at_utc) values($1,$2,$3,$4,$5,$6,now())", c, tx);
        Add(insert, id, scope, scope == "Direct" ? null : destination.ScopeId, scope == "Direct" ? key : null,
            scope == "Direct" ? email.FromName : destination.DisplayName, email.FromAddress); insert.ExecuteNonQuery();
        if (scope == "Direct")
        { using var member = new NpgsqlCommand("insert into studio_message_thread_members(thread_id,user_name,joined_at_utc) values($1,$2,now())", c, tx); Add(member, id, destination.UserName!); member.ExecuteNonQuery(); }
        tx.Commit(); return id;
    }
}

