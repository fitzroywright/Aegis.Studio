using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed record UnifiedSendRequest(Guid RequestId, Guid? ThreadId, string Subject, string Body, Guid[] DestinationIds);
public sealed record UnifiedSendResult(Guid MessageId, Guid ThreadId, string Status);
public sealed record StudioDeliveryStatus(Guid MessageId, string Recipient, string Transport, string Status, string? Detail, Guid? DeliveryId,DateTimeOffset? ProviderAcceptedAtUtc=null,string? ThreadingVerificationStatus=null);

public sealed class StudioUnifiedMessaging
{
    private readonly StudioDatabase database;
    private readonly StudioAddressBook addresses;
    private readonly StudioInAppMessageStore messages;
    private readonly StudioDeliveryPlanner planner;
    private readonly UserProfileStore profiles;
    private readonly StudioMessageAttachmentStore attachments;
    private readonly IConfiguration configuration;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    public StudioUnifiedMessaging(StudioDatabase database, StudioAddressBook addresses, StudioInAppMessageStore messages,
        StudioDeliveryPlanner planner, UserProfileStore profiles, StudioMessageAttachmentStore attachments, IConfiguration configuration,
        WorkspaceAccessService workspaceAccess, JobAccessService jobAccess)
    { this.database = database; this.addresses = addresses; this.messages = messages; this.planner = planner; this.profiles = profiles;
        this.attachments = attachments; this.configuration = configuration; this.workspaceAccess = workspaceAccess; this.jobAccess = jobAccess; }

    public void EnsureSchema()
    {
        using var stream = typeof(StudioUnifiedMessaging).Assembly.GetManifestResourceStream("Aegis.Studio.Web.Messaging.UnifiedMessagingSchema.sql")!;
        using var reader = new StreamReader(stream);
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        using var q = new NpgsqlCommand(reader.ReadToEnd(), c, tx); q.ExecuteNonQuery(); tx.Commit();
    }

    public async Task<UnifiedSendResult> SendAsync(UnifiedSendRequest request, IFormFileCollection files, ClaimsPrincipal user, CancellationToken token)
    {
        string sender = CurrentUser(user);
        if (request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 10000
            || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 200 || request.Subject.Contains('\n') || request.Subject.Contains('\r')
            || request.DestinationIds is null || request.DestinationIds.Length > 100 || files.Count > 10 || files.Sum(f => f.Length) > 2_500_000)
            throw new InvalidOperationException("Supply a request ID, subject, message, at most 100 destinations and attachments totaling at most 2.5 MB.");
        var destinations = request.DestinationIds.Distinct().Select(id => addresses.Get(id, user)).ToArray();
        Guid threadId = request.ThreadId ?? CreateThread(user, destinations, request.Subject);
        AssertSendAccess(threadId, user);
        // Direct conversation membership is explicit; a later send cannot secretly add new readers.
        var participants = messages.GetThreadParticipants(threadId);
        foreach (var destination in destinations.Where(d => d.DeliveryMode != "ExternalOnly"))
            if (destination.ContactType == "Staff" && (destination.UserName is null || !participants.Contains(destination.UserName, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Start a new conversation to include this staff recipient.");
        using(var scopeConnection=database.OpenConnection())using(var scopeQuery=new NpgsqlCommand("select scope_type,scope_id from studio_message_threads where id=$1",scopeConnection))
        {
            Add(scopeQuery,threadId);using var scopeReader=scopeQuery.ExecuteReader();scopeReader.Read();string scope=scopeReader.GetString(0);Guid? scopeId=scopeReader.IsDBNull(1)?null:scopeReader.GetGuid(1);
            if(destinations.Any(d=>(d.ContactType is "Job" or "Workspace") && (d.ContactType!=scope||d.ScopeId!=scopeId)))
                throw new InvalidOperationException("The destination must match this conversation's job or workspace.");
        }
        List<string> contentHashes=[];
        foreach(var file in files){using var content=new MemoryStream();await file.CopyToAsync(content,token);contentHashes.Add(Convert.ToHexString(SHA256.HashData(content.ToArray())));}
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            threadId, request.Subject, request.Body, destinations = destinations.Select(d => d.Id).Order().ToArray(),
            files = files.Select((f,index) => new { f.FileName, f.Length, hash=contentHashes[index] }).ToArray()
        }))));
        Guid messageId;
        using (var c = database.OpenConnection())
        using (var tx = c.BeginTransaction())
        {
            LockRequest(c, tx, sender.ToLowerInvariant() + request.RequestId);
            using var find = new NpgsqlCommand("select id,thread_id,request_hash from studio_inapp_messages where lower(sender_user_name)=lower($1) and client_request_id=$2", c, tx);
            find.Parameters.AddWithValue(sender); find.Parameters.AddWithValue(request.RequestId);
            using var r = find.ExecuteReader();
            if (r.Read())
            {
                messageId = r.GetGuid(0);
                if (r.GetGuid(1) != threadId || r.GetString(2) != fingerprint) throw new InvalidOperationException("The request ID was already used for a different message.");
                r.Close();
            }
            else
            {
                r.Close(); messageId = Guid.NewGuid();
                using var insert = new NpgsqlCommand("insert into studio_inapp_messages(id,thread_id,sender_user_name,body,created_at_utc,subject,recipient_addresses,client_request_id,request_hash,send_state) values($1,$2,$3,$4,now(),$5,$6,$7,$8,'AttachmentsPending')", c, tx);
                Add(insert, messageId, threadId, sender, request.Body.Trim(), request.Subject.Trim(), destinations.Select(d => string.IsNullOrWhiteSpace(d.EmailAddress) ? d.DisplayName : d.EmailAddress).ToArray(), request.RequestId, fingerprint); insert.ExecuteNonQuery();
                foreach (var destination in destinations)
                {
                    using var route = new NpgsqlCommand("insert into studio_message_route_recipients(message_id,destination_id) values($1,$2)", c, tx);
                    Add(route, messageId, destination.Id); route.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        // Queue external deliveries only after all files are durable. Reposting this request resumes an interrupted upload.
        try
        {
            foreach (var file in files)
            {
                using var content = new MemoryStream(); await file.CopyToAsync(content, token);
                string hash = Convert.ToHexString(SHA256.HashData(content.ToArray()));
                await attachments.AddAsync(messageId, file, sender, token, $"compose:{messageId:D}:{file.FileName}:{hash}");
            }
        }
        catch
        {
            using var failureConnection=database.OpenConnection();using var failureTransaction=failureConnection.BeginTransaction();
            Ledger(failureConnection,failureTransaction,messageId,"Attachments","Routing","Attention",null,"Attachment upload is incomplete. Repost the original request with its original files to resume.");
            failureTransaction.Commit();throw;
        }
        using(var c=database.OpenConnection())using(var tx=c.BeginTransaction())
        {
            using(var ready=new NpgsqlCommand("update studio_inapp_messages set send_state='Ready' where id=$1",c,tx)){Add(ready,messageId);ready.ExecuteNonQuery();}
            using(var clear=new NpgsqlCommand("delete from studio_message_deliveries where message_id=$1 and transport='Routing' and recipient='Attachments'",c,tx)){Add(clear,messageId);clear.ExecuteNonQuery();}
            tx.Commit();
        }
        return await RouteAsync(messageId, threadId, destinations, user, token);
    }

    public async Task<UnifiedSendResult> RetryRoutingAsync(Guid messageId, ClaimsPrincipal user, CancellationToken token)
    {
        Guid threadId = attachments.GetThreadId(messageId) ?? throw new KeyNotFoundException();
        AssertSendAccess(threadId, user);
        using var c = database.OpenConnection();
        using var q = new NpgsqlCommand("select destination_id from studio_message_route_recipients where message_id=$1", c);
        Add(q, messageId); using var r = q.ExecuteReader(); List<Guid> ids = []; while (r.Read()) ids.Add(r.GetGuid(0)); r.Close();
        return await RouteAsync(messageId, threadId, ids.Select(id => addresses.Get(id, user)).ToArray(), user, token);
    }

    private async Task<UnifiedSendResult> RouteAsync(Guid messageId, Guid threadId, IReadOnlyList<StudioAddressEntry> destinations, ClaimsPrincipal user, CancellationToken token)
    {
        var plan = await planner.PlanAsync(destinations, token);
        var senderProfile = profiles.GetOrCreate(user);
        var sender = addresses.GetAll().FirstOrDefault(e => e.ContactType == "Staff" && string.Equals(e.UserName, senderProfile.UserName, StringComparison.OrdinalIgnoreCase));
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        LockRequest(c, tx, messageId.ToString("D"));
        using var find = new NpgsqlCommand("select sender_user_name,body,subject,send_state from studio_inapp_messages where id=$1", c, tx);
        Add(find, messageId); using var r = find.ExecuteReader(); if (!r.Read()) throw new KeyNotFoundException();
        string originalSender = r.GetString(0), body = r.GetString(1), subject = r.GetString(2), sendState=r.GetString(3); r.Close();
        if(sendState!="Ready")throw new InvalidOperationException("Attachment upload is incomplete. Repost the original message with its original files to resume.");
        if (!string.Equals(originalSender, CurrentUser(user), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Only the original sender can resume routing.");
        using(var frozen=new NpgsqlCommand("select exists(select 1 from studio_outbound_email where studio_message_id=$1)",c,tx))
        {
            Add(frozen,messageId);if((bool)frozen.ExecuteScalar()!){tx.Commit();return new(messageId,threadId,"Queued");}
        }
        // One canonical message, explicit per-destination internal delivery ledger; no notification email is generated here.
        foreach (var destination in destinations.Where(d => d.Status == "Active" && d.DeliveryMode != "ExternalOnly"))
            Ledger(c, tx, messageId, destination.StudioInboxId!, "Internal", "Delivered", destination.Id, null);
        foreach (var exclusion in plan.Exclusions) Ledger(c, tx, messageId, exclusion.Recipient, "Email", "Excluded", exclusion.DestinationId, exclusion.Reason);
        foreach (var attention in plan.Attention) Ledger(c, tx, messageId, attention.DestinationId.ToString("D"), "Routing", "Attention", attention.DestinationId, attention.Reason);
        bool blocked = plan.Attention.Count > 0;
        string monitor = configuration["Messaging:MicrosoftGraph:Email:SenderUpn"] ?? "";
        if (plan.ExternalRecipients.Count > 0 && (sender is null || sender.SenderVerified != true || sender.Status!="Active" || !CanSendAsSelf(threadId,user) || string.IsNullOrWhiteSpace(sender.SenderMailbox)
            || string.IsNullOrWhiteSpace(sender.EmailAddress) || string.IsNullOrWhiteSpace(monitor) || !configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false)))
        {
            blocked = true; Ledger(c, tx, messageId, "Sender", "Routing", "Attention", sender?.Id, "External sending requires a tenant-verified professional sender, authorized mailbox, enabled transport and monitored Reply-To.");
        }
        if (!blocked)
        {
            using (var clear = new NpgsqlCommand("delete from studio_message_deliveries where message_id=$1 and transport='Routing'", c, tx)) { Add(clear, messageId); clear.ExecuteNonQuery(); }
            string inReplyTo = LastInboundMessageId(c, tx, threadId);
            foreach (string recipient in plan.ExternalRecipients)
            {
                var authorizedSender=sender??throw new UnauthorizedAccessException("No authorized professional sender is configured.");
                Guid deliveryId = Guid.NewGuid(); string rfcId = $"<studio.{messageId:N}.{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recipient)))[..16].ToLowerInvariant()}@{authorizedSender.EmailAddress.Split('@')[1]}>";
                var metadata = new Dictionary<string, string> { ["Email.MessageId"] = rfcId, ["Studio.MessageId"] = messageId.ToString("D"),
                    ["Email.From"] = authorizedSender.EmailAddress, ["Email.FromName"] = authorizedSender.DisplayName, ["Email.ReplyTo"] = monitor, ["Email.InReplyTo"] = inReplyTo, ["Email.References"] = inReplyTo };
                using var insert = new NpgsqlCommand("""
                    insert into studio_outbound_email(id,conversation_id,studio_thread_id,studio_message_id,from_address,to_address,subject,body,status,created_at_utc,kind,sent_by_user_name,sending_identity_type,delivery_channel,delivery_metadata,rfc_message_id,sender_mailbox)
                    values($1,null,$2,$3,$4,$5,$6,$7,'Pending',now(),'Unified',$8,'Self','Email',$9::jsonb,$10,$11)
                    on conflict(studio_message_id,lower(to_address),delivery_channel) where studio_message_id is not null do nothing
                    """, c, tx);
                Add(insert, deliveryId, threadId, messageId, authorizedSender.EmailAddress, recipient, subject, body, originalSender, JsonSerializer.Serialize(metadata), rfcId, authorizedSender.SenderMailbox); insert.ExecuteNonQuery();
                Ledger(c, tx, messageId, recipient, "Email", "Queued", null, null);
            }
        }
        tx.Commit();
        return new(messageId, threadId, blocked ? "Attention" : plan.ExternalRecipients.Count > 0 ? "Queued" : "DeliveredInternally");
    }

    private Guid CreateThread(ClaimsPrincipal user, IReadOnlyList<StudioAddressEntry> destinations, string title)
    {
        if (destinations.Any(d=>d.Status!="Active"))throw new InvalidOperationException("Choose active destinations; inactive entries require administrator review.");
        if (destinations.Count == 0) throw new InvalidOperationException("Choose at least one recipient.");
        var scoped = destinations.Where(d => d.ContactType is "Job" or "Workspace").ToArray();
        if (scoped.Length > 0)
        {
            if (scoped.Length != 1) throw new InvalidOperationException("Choose one job or workspace conversation at a time.");
            return messages.GetOrCreateScoped(user, scoped[0].ContactType, scoped[0].ScopeId!.Value).Id;
        }
        string current = CurrentUser(user);
        var members = destinations.Where(d => d.ContactType == "Staff" && d.DeliveryMode != "ExternalOnly").Select(d => d.UserName!)
            .Append(current).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // Retain the existing one-to-one staff key, so composing does not create a competing private inbox.
        string key = destinations.All(d => d.ContactType == "Staff") && members.Length == 2
            ? string.Join('|', members.Select(x => x.ToLowerInvariant()).Order(StringComparer.Ordinal))
            : "unified:" + current.ToLowerInvariant() + ":" + string.Join(',', destinations.Select(d => d.Id.ToString("N")).Order(StringComparer.Ordinal));
        using var c = database.OpenConnection(); using var tx = c.BeginTransaction();
        LockRequest(c, tx, key);
        using var q = new NpgsqlCommand("insert into studio_message_threads(id,scope_type,direct_key,title,created_by,created_at_utc) values($1,'Direct',$2,$3,$4,now()) on conflict(direct_key) where direct_key is not null do update set direct_key=excluded.direct_key returning id", c, tx);
        Add(q, Guid.NewGuid(), key, title.Trim(), current); Guid id = (Guid)q.ExecuteScalar()!;
        foreach (var member in members)
        {
            using var m = new NpgsqlCommand("insert into studio_message_thread_members(thread_id,user_name,joined_at_utc) values($1,$2,now()) on conflict do nothing", c, tx); Add(m, id, member); m.ExecuteNonQuery();
        }
        tx.Commit(); return id;
    }

    public IReadOnlyList<StudioDeliveryStatus> GetDeliveries(Guid threadId, ClaimsPrincipal user)
    {
        messages.AssertThreadAccess(threadId, user);
        using var c = database.OpenConnection(); using var q = new NpgsqlCommand("""
            select d.message_id,d.recipient,d.transport,coalesce(e.status,d.status),coalesce(e.last_error,d.detail),e.id,e.provider_accepted_at_utc,case when e.rfc_message_id_verified then 'Verified' when e.provider_accepted_at_utc is not null then 'Pending' else 'NotSubmitted' end
            from studio_message_deliveries d join studio_inapp_messages m on m.id=d.message_id
            left join studio_outbound_email e on e.studio_message_id=d.message_id and lower(e.to_address)=lower(d.recipient) and d.transport='Email'
            where m.thread_id=$1 order by d.created_at_utc
            """, c);
        Add(q, threadId); using var r = q.ExecuteReader(); List<StudioDeliveryStatus> result = [];
        while (r.Read()) result.Add(new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5),r.IsDBNull(6)?null:r.GetFieldValue<DateTimeOffset>(6),r.GetString(7)));
        return result;
    }

    public void RetryDelivery(Guid deliveryId, ClaimsPrincipal user, bool reconciledNotSent)
    {
        using var c = database.OpenConnection(); using var q = new NpgsqlCommand("select studio_thread_id,status,last_error,provider_accepted_at_utc from studio_outbound_email where id=$1", c);
        Add(q, deliveryId); using var r = q.ExecuteReader(); if (!r.Read() || r.IsDBNull(0)) throw new KeyNotFoundException();
        Guid threadId = r.GetGuid(0); string status = r.GetString(1); string? error=r.IsDBNull(2)?null:r.GetString(2); bool accepted=!r.IsDBNull(3);r.Close(); AssertSendAccess(threadId, user);
        if(accepted)throw new InvalidOperationException("The provider already accepted this delivery. Reconcile threading instead of resending.");
        if (status is not ("Failed" or "Attention")) throw new InvalidOperationException("Only failed or held deliveries can be retried.");
        if (status == "Attention" && error?.StartsWith("ProviderOutcomeUncertain",StringComparison.Ordinal)==true && !reconciledNotSent) throw new InvalidOperationException("Check provider sent items or message trace and confirm the message was not sent before retrying an uncertain outcome.");
        using var retry = new NpgsqlCommand("update studio_outbound_email set status='Retry',attempts=0,next_attempt_at_utc=now(),last_error=null where id=$1 and status=$2 and provider_accepted_at_utc is null", c); Add(retry, deliveryId, status); retry.ExecuteNonQuery();
    }

    private bool CanSendAsSelf(Guid threadId,ClaimsPrincipal user)
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select t.scope_type,case when t.scope_type='Workspace' then t.scope_id else j.workspace_id end from studio_message_threads t left join studio_jobs j on t.scope_type='Job' and j.id=t.scope_id where t.id=$1",c);
        Add(q,threadId);using var r=q.ExecuteReader();if(!r.Read())return false;string scope=r.GetString(0);Guid? workspaceId=r.IsDBNull(1)?null:r.GetGuid(1);r.Close();
        return scope=="Direct" || (workspaceId.HasValue&&workspaceAccess.HasPermission(user,workspaceId.Value,"SendAsSelf"));
    }

    public void AssertSendAccess(Guid threadId, ClaimsPrincipal user)
    {
        messages.AssertThreadAccess(threadId, user);
        using var c = database.OpenConnection(); using var q = new NpgsqlCommand("select scope_type,scope_id from studio_message_threads where id=$1", c); Add(q, threadId);
        using var r = q.ExecuteReader(); r.Read(); string scope = r.GetString(0); Guid? id = r.IsDBNull(1) ? null : r.GetGuid(1); r.Close();
        if (scope == "Workspace" && !workspaceAccess.HasPermission(user, id!.Value, "ReplyRequester")) throw new UnauthorizedAccessException();
        if (scope == "Job" && !jobAccess.HasPermission(user, id!.Value, "ReplyRequester")) throw new UnauthorizedAccessException();
    }
    internal static string LastInboundMessageId(NpgsqlConnection c, NpgsqlTransaction tx, Guid threadId)
    { using var q = new NpgsqlCommand("select internet_message_id from studio_inapp_messages where thread_id=$1 and internet_message_id is not null order by created_at_utc desc limit 1", c, tx); Add(q, threadId); return q.ExecuteScalar() as string ?? ""; }
    internal static void Ledger(NpgsqlConnection c, NpgsqlTransaction tx, Guid messageId, string recipient, string transport, string status, Guid? destinationId, string? detail)
    { using var q = new NpgsqlCommand("insert into studio_message_deliveries(message_id,recipient,transport,status,destination_id,detail) values($1,$2,$3,$4,$5,$6) on conflict(message_id,recipient,transport) do update set status=excluded.status,detail=excluded.detail", c, tx); Add(q, messageId, recipient, transport, status, destinationId, detail); q.ExecuteNonQuery(); }
    internal static void LockRequest(NpgsqlConnection c, NpgsqlTransaction tx, string key)
    { using var q = new NpgsqlCommand("select pg_advisory_xact_lock(hashtextextended($1,0))", c, tx); Add(q, key); q.ExecuteNonQuery(); }
    internal static void Add(NpgsqlCommand q, params object?[] values) { foreach (var value in values) q.Parameters.AddWithValue(value ?? DBNull.Value); }
    internal static string CurrentUser(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.Identity?.Name ?? throw new UnauthorizedAccessException();
}

