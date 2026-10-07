using System.Security.Claims;
using System.Text.RegularExpressions;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Aegis.Studio.Web.Messaging;

public sealed class StudioMessageHub : Hub
{
    private readonly StudioInAppMessageStore store;

    public StudioMessageHub(StudioInAppMessageStore store)
    {
        this.store = store;
    }

    public override async Task OnConnectedAsync()
    {
        string userName = Context.User?.FindFirstValue("login_name") ?? Context.User?.Identity?.Name ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(userName)) await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userName));
        await base.OnConnectedAsync();
    }

    public async Task JoinThread(Guid threadId)
    {
        store.AssertThreadAccess(threadId, Context.User ?? throw new HubException("Authentication is required."));
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(CurrentUser()));
        await Groups.AddToGroupAsync(Context.ConnectionId, ThreadGroup(threadId));
    }

    public Task LeaveThread(Guid threadId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ThreadGroup(threadId));

    private string CurrentUser() => Context.User?.FindFirstValue("login_name") ?? Context.User?.Identity?.Name ?? throw new HubException("Authentication is required.");

    internal static string UserGroup(string userName) => $"user:{userName.Trim().ToLowerInvariant()}";
    internal static string ThreadGroup(Guid threadId) => $"thread:{threadId:D}";
}

public sealed class StudioInAppMessageStore
{
    private readonly StudioDatabase database;
    private readonly UserProfileStore profiles;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    private readonly StudioStore studio;
    private readonly StudioMessageAddressBook addresses;

    public StudioInAppMessageStore(
        StudioDatabase database,
        UserProfileStore profiles,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess,
        StudioStore studio,
        StudioMessageAddressBook addresses)
    {
        this.database = database;
        this.profiles = profiles;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
        this.studio = studio;
        this.addresses = addresses;
    }

    public IReadOnlyList<StudioMessageThreadSummary> GetThreads(ClaimsPrincipal user)
    {
        string userName = CurrentUser(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select t.id,t.scope_type,t.scope_id,
                   case when t.scope_type='Direct' then coalesce(
                       (select dm.user_name from studio_message_thread_members dm where dm.thread_id=t.id and lower(dm.user_name)<>lower($1) order by dm.user_name limit 1),
                       t.title)
                   else t.title end as display_title,
                   t.created_by,t.created_at_utc,
                   max(m.created_at_utc) as last_message_at,
                   count(m.id) filter (where m.created_at_utc > coalesce(r.last_read_at_utc,'epoch'::timestamptz) and lower(m.sender_user_name)<>lower($1)) as unread_count
            from studio_message_threads t
            left join studio_message_thread_members tm on tm.thread_id=t.id and lower(tm.user_name)=lower($1)
            left join studio_message_reads r on r.thread_id=t.id and lower(r.user_name)=lower($1)
            left join studio_inapp_messages m on m.thread_id=t.id
            where (t.scope_type='Direct' and tm.user_name is not null)
               or (t.scope_type='Workspace' and exists(select 1 from studio_workspace_members wm where wm.workspace_id=t.scope_id and lower(wm.user_name)=lower($1) and wm.is_active))
               or (t.scope_type='Job' and exists(select 1 from studio_job_members jm where jm.job_id=t.scope_id and lower(jm.user_name)=lower($1) and jm.is_active))
               or (t.scope_type='Person' and tm.user_name is not null)
            group by t.id,t.scope_type,t.scope_id,t.title,t.created_by,t.created_at_utc,r.last_read_at_utc
            order by coalesce(max(m.created_at_utc),t.created_at_utc) desc
            """;
        command.Parameters.AddWithValue(userName);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioMessageThreadSummary> values = [];
        while (reader.Read())
        {
            values.Add(new StudioMessageThreadSummary(
                reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7)));
        }
        return values;
    }

    public StudioMessageThread CreateDirect(ClaimsPrincipal user, string otherUserName)
    {
        string current = CurrentUser(user);
        string other = otherUserName.Trim();
        if (string.IsNullOrWhiteSpace(other) || string.Equals(current, other, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different Studio user is required for a direct message.");
        if (!profiles.GetAll().Any(x => string.Equals(x.UserName, other, StringComparison.OrdinalIgnoreCase)))
            throw new KeyNotFoundException("Studio user was not found.");

        string key = string.Join('|', new[] { current.ToLowerInvariant(), other.ToLowerInvariant() }.OrderBy(x => x, StringComparer.Ordinal));
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using NpgsqlCommand existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "select id,title,created_by,created_at_utc from studio_message_threads where scope_type='Direct' and direct_key=$1";
        existing.Parameters.AddWithValue(key);
        using NpgsqlDataReader reader = existing.ExecuteReader();
        if (reader.Read())
        {
            StudioMessageThread found = new(reader.GetGuid(0), "Direct", null, other, reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
            reader.Close();
            transaction.Commit();
            return found;
        }
        reader.Close();

        Guid id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using NpgsqlCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "insert into studio_message_threads(id,scope_type,scope_id,direct_key,title,created_by,created_at_utc) values($1,'Direct',null,$2,$3,$4,$5)";
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(key);
        insert.Parameters.AddWithValue(other);
        insert.Parameters.AddWithValue(current);
        insert.Parameters.AddWithValue(now);
        insert.ExecuteNonQuery();
        AddDirectMember(connection, transaction, id, current, now);
        AddDirectMember(connection, transaction, id, other, now);
        transaction.Commit();
        return new StudioMessageThread(id, "Direct", null, other, current, now);
    }

    public StudioMessageThread CreateExternal(ClaimsPrincipal user, string address, string subject)
    {
        string current = CurrentUser(user);
        string recipient = address.Trim();
        if (!StudioMessageAddressBook.IsExternalEmail(recipient))
            throw new InvalidOperationException("A valid external email address is required.");

        StudioMessageAddress? internalDestination = addresses.ResolveAddress(recipient);
        if (internalDestination?.DestinationType == "Person" && !string.IsNullOrWhiteSpace(internalDestination.PersonUserName))
            return CreateDirect(user, internalDestination.PersonUserName);
        if (internalDestination?.DestinationType == "Workspace" && internalDestination.WorkspaceId.HasValue)
            return GetOrCreateScoped(user, "Workspace", internalDestination.WorkspaceId.Value);
        if (internalDestination?.DestinationType == "Job" && internalDestination.JobId.HasValue)
            return GetOrCreateScoped(user, "Job", internalDestination.JobId.Value);

        StudioMessageAddress person = addresses.EnsureCurrentPerson(user);
        string title = string.IsNullOrWhiteSpace(subject) ? recipient : subject.Trim();
        Guid conversationId = Guid.NewGuid();
        Guid threadId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                insert into studio_conversations
                    (id,workspace_id,subject,contact_name,contact_address,channel,is_closed,job_id,external_thread_id,created_at_utc)
                values($1,$2,$3,$4,$4,'Email',false,null,null,$5)
                """;
            conversation.Parameters.AddWithValue(conversationId);
            conversation.Parameters.AddWithValue(person.WorkspaceId!.Value);
            conversation.Parameters.AddWithValue(title);
            conversation.Parameters.AddWithValue(recipient);
            conversation.Parameters.AddWithValue(now);
            conversation.ExecuteNonQuery();
        }
        using (NpgsqlCommand thread = connection.CreateCommand())
        {
            thread.Transaction = transaction;
            thread.CommandText = """
                insert into studio_message_threads
                    (id,scope_type,scope_id,direct_key,title,created_by,created_at_utc,external_conversation_id,owner_user_name,external_address)
                values($1,'Person',$2,null,$3,$4,$5,$2,$4,$6)
                """;
            thread.Parameters.AddWithValue(threadId);
            thread.Parameters.AddWithValue(conversationId);
            thread.Parameters.AddWithValue(title);
            thread.Parameters.AddWithValue(current);
            thread.Parameters.AddWithValue(now);
            thread.Parameters.AddWithValue(recipient);
            thread.ExecuteNonQuery();
        }
        AddDirectMember(connection, transaction, threadId, current, now);
        transaction.Commit();
        return new StudioMessageThread(threadId, "Person", conversationId, title, current, now);
    }

    public StudioExternalThread? GetExternalThread(Guid threadId, ClaimsPrincipal user)
    {
        AssertThreadAccess(threadId, user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select t.external_conversation_id,t.external_address,t.owner_user_name,c.subject
            from studio_message_threads t
            join studio_conversations c on c.id=t.external_conversation_id
            where t.id=$1 and t.scope_type='Person' and t.external_conversation_id is not null
            """;
        command.Parameters.AddWithValue(threadId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new StudioExternalThread(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3))
            : null;
    }

    public StudioMessageThread EnsurePersonThread(StudioConversation conversation, string ownerUserName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand find = connection.CreateCommand();
        find.CommandText = """
            select id,title,created_by,created_at_utc
            from studio_message_threads
            where scope_type='Person' and external_conversation_id=$1
            """;
        find.Parameters.AddWithValue(conversation.Id);
        using NpgsqlDataReader reader = find.ExecuteReader();
        if (reader.Read())
            return new StudioMessageThread(reader.GetGuid(0),"Person",conversation.Id,reader.GetString(1),reader.GetString(2),reader.GetFieldValue<DateTimeOffset>(3));
        reader.Close();

        Guid id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using NpgsqlTransaction tx = connection.BeginTransaction();
        using NpgsqlCommand insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            insert into studio_message_threads
                (id,scope_type,scope_id,direct_key,title,created_by,created_at_utc,external_conversation_id,owner_user_name,external_address)
            values($1,'Person',$2,null,$3,$4,$5,$2,$4,$6)
            """;
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(conversation.Id);
        insert.Parameters.AddWithValue(conversation.Subject);
        insert.Parameters.AddWithValue(ownerUserName);
        insert.Parameters.AddWithValue(now);
        insert.Parameters.AddWithValue(conversation.ContactAddress);
        insert.ExecuteNonQuery();
        AddDirectMember(connection,tx,id,ownerUserName,now);
        tx.Commit();
        return new StudioMessageThread(id,"Person",conversation.Id,conversation.Subject,ownerUserName,now);
    }

    public StudioInAppMessage AddExternalMessage(Guid threadId, InboundEmail email)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand duplicate = connection.CreateCommand();
        duplicate.CommandText = "select id,thread_id,sender_user_name,body,created_at_utc,sender_display_name,sender_address from studio_inapp_messages where external_message_id=$1";
        duplicate.Parameters.AddWithValue(email.MessageId);
        using NpgsqlDataReader existing = duplicate.ExecuteReader();
        if (existing.Read())
            return new StudioInAppMessage(existing.GetGuid(0),existing.GetGuid(1),existing.GetString(2),existing.GetString(3),existing.GetFieldValue<DateTimeOffset>(4),
                existing.IsDBNull(5)?null:existing.GetString(5),existing.IsDBNull(6)?null:existing.GetString(6));
        existing.Close();

        StudioInAppMessage message = new(Guid.NewGuid(),threadId,email.FromAddress.Trim(),email.Body.Trim(),email.ReceivedAt.ToUniversalTime(),
            string.IsNullOrWhiteSpace(email.FromName)?email.FromAddress.Trim():email.FromName.Trim(),email.FromAddress.Trim());
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_inapp_messages
                (id,thread_id,sender_user_name,body,created_at_utc,sender_display_name,sender_address,external_message_id)
            values($1,$2,$3,$4,$5,$6,$7,$8)
            """;
        command.Parameters.AddWithValue(message.Id);
        command.Parameters.AddWithValue(threadId);
        command.Parameters.AddWithValue(message.SenderUserName);
        command.Parameters.AddWithValue(message.Body);
        command.Parameters.AddWithValue(message.CreatedAtUtc);
        command.Parameters.AddWithValue((object?)message.SenderDisplayName??DBNull.Value);
        command.Parameters.AddWithValue((object?)message.SenderAddress??DBNull.Value);
        command.Parameters.AddWithValue(email.MessageId);
        command.ExecuteNonQuery();
        return message;
    }

    public StudioMessageThread GetOrCreateScoped(ClaimsPrincipal user, string scopeType, Guid scopeId)
    {
        string current = CurrentUser(user);
        string normalized = scopeType.Equals("Workspace", StringComparison.OrdinalIgnoreCase) ? "Workspace" : scopeType.Equals("Job", StringComparison.OrdinalIgnoreCase) ? "Job" : throw new InvalidOperationException("Scope must be Workspace or Job.");
        EnsureScopeAccess(user, normalized, scopeId);
        string title = normalized == "Workspace" ? studio.GetWorkspaces().FirstOrDefault(x => x.Id == scopeId)?.Name ?? "Workspace team" : studio.GetJob(scopeId)?.Title ?? "Job team";
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand find = connection.CreateCommand();
        find.CommandText = "select id,title,created_by,created_at_utc from studio_message_threads where scope_type=$1 and scope_id=$2";
        find.Parameters.AddWithValue(normalized);
        find.Parameters.AddWithValue(scopeId);
        using NpgsqlDataReader reader = find.ExecuteReader();
        if (reader.Read()) return new StudioMessageThread(reader.GetGuid(0), normalized, scopeId, reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        reader.Close();
        Guid id = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using NpgsqlCommand insert = connection.CreateCommand();
        insert.CommandText = "insert into studio_message_threads(id,scope_type,scope_id,direct_key,title,created_by,created_at_utc) values($1,$2,$3,null,$4,$5,$6) returning id";
        insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(normalized); insert.Parameters.AddWithValue(scopeId); insert.Parameters.AddWithValue(title); insert.Parameters.AddWithValue(current); insert.Parameters.AddWithValue(now);
        insert.ExecuteScalar();
        return new StudioMessageThread(id, normalized, scopeId, title, current, now);
    }

    public IReadOnlyList<StudioInAppMessage> GetMessages(Guid threadId, ClaimsPrincipal user, int count = 100)
    {
        AssertThreadAccess(threadId, user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,thread_id,sender_user_name,body,created_at_utc,sender_display_name,sender_address from studio_inapp_messages where thread_id=$1 order by created_at_utc desc limit $2";
        command.Parameters.AddWithValue(threadId); command.Parameters.AddWithValue(Math.Clamp(count, 1, 500));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioInAppMessage> values = [];
        while (reader.Read()) values.Add(new StudioInAppMessage(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5)?null:reader.GetString(5), reader.IsDBNull(6)?null:reader.GetString(6)));
        values.Reverse();
        return values;
    }

    public StudioInAppMessage AddMessage(Guid threadId, ClaimsPrincipal user, string body)
    {
        AssertThreadAccess(threadId, user);
        string current = CurrentUser(user);
        string text = body.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Message body is required.");
        if (text.Length > 10000) throw new InvalidOperationException("Message body cannot exceed 10,000 characters.");
        StudioInAppMessage message = new(Guid.NewGuid(), threadId, current, text, DateTimeOffset.UtcNow);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_inapp_messages(id,thread_id,sender_user_name,body,created_at_utc) values($1,$2,$3,$4,$5)";
        command.Parameters.AddWithValue(message.Id); command.Parameters.AddWithValue(threadId); command.Parameters.AddWithValue(current); command.Parameters.AddWithValue(text); command.Parameters.AddWithValue(message.CreatedAtUtc);
        command.ExecuteNonQuery();
        return message;
    }

    public IReadOnlyList<string> ResolveMentionedParticipants(Guid threadId, string senderUserName, string body)
    {
        List<string> mentioned = [];
        foreach (string participant in GetThreadParticipants(threadId))
        {
            if (string.Equals(participant, senderUserName, StringComparison.OrdinalIgnoreCase)) continue;
            string pattern = $@"(?<!\S)@{Regex.Escape(participant)}(?=$|\s|[.,!?;:)\]])";
            if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) mentioned.Add(participant);
        }
        return mentioned.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void MarkRead(Guid threadId, ClaimsPrincipal user)
    {
        AssertThreadAccess(threadId, user);
        string current = CurrentUser(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_message_reads(thread_id,user_name,last_read_at_utc) values($1,$2,now()) on conflict(thread_id,user_name) do update set last_read_at_utc=excluded.last_read_at_utc";
        command.Parameters.AddWithValue(threadId); command.Parameters.AddWithValue(current); command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetThreadParticipants(Guid threadId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand scope = connection.CreateCommand();
        scope.CommandText = "select scope_type,scope_id from studio_message_threads where id=$1";
        scope.Parameters.AddWithValue(threadId);
        using NpgsqlDataReader scopeReader = scope.ExecuteReader();
        if (!scopeReader.Read()) return [];
        string scopeType = scopeReader.GetString(0);
        Guid? scopeId = scopeReader.IsDBNull(1) ? null : scopeReader.GetGuid(1);
        scopeReader.Close();

        using NpgsqlCommand command = connection.CreateCommand();
        if (scopeType is "Direct" or "Person")
        {
            command.CommandText = "select user_name from studio_message_thread_members where thread_id=$1 order by user_name";
            command.Parameters.AddWithValue(threadId);
        }
        else if (scopeType == "Workspace" && scopeId.HasValue)
        {
            command.CommandText = "select user_name from studio_workspace_members where workspace_id=$1 and is_active order by user_name";
            command.Parameters.AddWithValue(scopeId.Value);
        }
        else if (scopeType == "Job" && scopeId.HasValue)
        {
            command.CommandText = "select user_name from studio_job_members where job_id=$1 and is_active order by user_name";
            command.Parameters.AddWithValue(scopeId.Value);
        }
        else
        {
            return [];
        }

        using NpgsqlDataReader reader = command.ExecuteReader();
        List<string> users = [];
        while (reader.Read()) users.Add(reader.GetString(0));
        return users.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void AssertThreadAccess(Guid threadId, ClaimsPrincipal user)
    {
        string current = CurrentUser(user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select scope_type,scope_id from studio_message_threads where id=$1";
        command.Parameters.AddWithValue(threadId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Message thread was not found.");
        string scopeType = reader.GetString(0);
        Guid? scopeId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        reader.Close();
        if (scopeType is "Direct" or "Person")
        {
            using NpgsqlCommand member = connection.CreateCommand();
            member.CommandText = "select exists(select 1 from studio_message_thread_members where thread_id=$1 and lower(user_name)=lower($2))";
            member.Parameters.AddWithValue(threadId); member.Parameters.AddWithValue(current);
            if (!(bool)(member.ExecuteScalar() ?? false)) throw new UnauthorizedAccessException();
            return;
        }
        if (!scopeId.HasValue) throw new UnauthorizedAccessException();
        EnsureScopeAccess(user, scopeType, scopeId.Value);
    }

    private void EnsureScopeAccess(ClaimsPrincipal user, string scopeType, Guid scopeId)
    {
        bool allowed = scopeType == "Workspace" ? workspaceAccess.HasPermission(user, scopeId, "View") : jobAccess.HasPermission(user, scopeId, "ViewJob");
        if (!allowed) throw new UnauthorizedAccessException();
    }

    private static void AddDirectMember(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid threadId, string userName, DateTimeOffset now)
    {
        using NpgsqlCommand member = connection.CreateCommand();
        member.Transaction = transaction;
        member.CommandText = "insert into studio_message_thread_members(thread_id,user_name,joined_at_utc) values($1,$2,$3)";
        member.Parameters.AddWithValue(threadId); member.Parameters.AddWithValue(userName); member.Parameters.AddWithValue(now); member.ExecuteNonQuery();
    }

    private static string CurrentUser(ClaimsPrincipal user) => user.FindFirstValue("login_name") ?? user.Identity?.Name ?? throw new UnauthorizedAccessException();
}

public static class StudioInAppMessagingEndpoints
{
    public static void MapStudioInAppMessaging(this WebApplication app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/messages").RequireAuthorization();
        group.MapGet("/threads", (ClaimsPrincipal user, StudioInAppMessageStore store) => Results.Ok(store.GetThreads(user)));
        group.MapGet("/users", (ClaimsPrincipal user, UserProfileStore profiles) =>
        {
            string current = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? string.Empty;
            return Results.Ok(profiles.GetAll()
                .Where(x => !string.Equals(x.UserName, current, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.DisplayName)
                .Select(x => new StudioMessageUser(x.UserName, x.DisplayName)));
        });
        group.MapPost("/threads/direct", (StartDirectMessageRequest request, ClaimsPrincipal user, StudioInAppMessageStore store) => Execute(() => Results.Ok(store.CreateDirect(user, request.UserName))));
        group.MapPost("/threads/{scopeType}/{scopeId:guid}", (string scopeType, Guid scopeId, ClaimsPrincipal user, StudioInAppMessageStore store) => Execute(() => Results.Ok(store.GetOrCreateScoped(user, scopeType, scopeId))));
        group.MapGet("/threads/{threadId:guid}", (Guid threadId, int? count, ClaimsPrincipal user, StudioInAppMessageStore store) => Execute(() => Results.Ok(store.GetMessages(threadId, user, count ?? 100))));
        group.MapPost("/threads/{threadId:guid}/whatsapp", (Guid threadId, SendStudioMessageRequest request, ClaimsPrincipal user, StudioInAppMessageStore store, NotificationService notifications) =>
        {
            try
            {
                store.AssertThreadAccess(threadId, user);
                string current = user.FindFirstValue("login_name") ?? user.Identity?.Name ?? throw new UnauthorizedAccessException();
                string recipient = store.GetThreadParticipants(threadId)
                    .FirstOrDefault(name => !string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("A photographer recipient could not be identified.");

                notifications.SendWhatsAppToUser(
                    recipient,
                    "Studio message",
                    request.Body,
                    "MessageThread",
                    threadId.ToString("D"));

                return Results.Accepted();
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/threads/{threadId:guid}", async (Guid threadId, SendStudioMessageRequest request, ClaimsPrincipal user, StudioInAppMessageStore store, NotificationService notifications, IHubContext<StudioMessageHub> hub) =>
        {
            try
            {
                StudioInAppMessage message = store.AddMessage(threadId, user, request.Body);
                IReadOnlyList<string> mentionedUsers = store.ResolveMentionedParticipants(threadId, message.SenderUserName, message.Body);
                foreach (string mentionedUser in mentionedUsers)
                {
                    notifications.PublishToUser(
                        mentionedUser,
                        "Mention",
                        $"{message.SenderUserName} mentioned you",
                        message.Body,
                        "MessageThread",
                        threadId.ToString("D"));
                    await hub.Clients.Group(StudioMessageHub.UserGroup(mentionedUser)).SendAsync("mentionReceived", new
                    {
                        threadId,
                        messageId = message.Id,
                        senderUserName = message.SenderUserName,
                        body = message.Body,
                        createdAtUtc = message.CreatedAtUtc
                    });
                }

                await hub.Clients.Group(StudioMessageHub.ThreadGroup(threadId)).SendAsync("messageReceived", message);
                foreach (string participant in store.GetThreadParticipants(threadId))
                    await hub.Clients.Group(StudioMessageHub.UserGroup(participant)).SendAsync("threadChanged", threadId);
                return Results.Ok(message);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        group.MapPost("/threads/{threadId:guid}/read", (Guid threadId, ClaimsPrincipal user, StudioInAppMessageStore store) => Execute(() => { store.MarkRead(threadId, user); return Results.NoContent(); }));
        app.MapHub<StudioMessageHub>("/hubs/messages").RequireAuthorization();
    }

    private static IResult Execute(Func<IResult> action)
    {
        try { return action(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
}

public sealed record StudioMessageThread(Guid Id, string ScopeType, Guid? ScopeId, string Title, string CreatedBy, DateTimeOffset CreatedAtUtc);
public sealed record StudioMessageThreadSummary(Guid Id, string ScopeType, Guid? ScopeId, string Title, string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastMessageAtUtc, long UnreadCount);
public sealed record StudioInAppMessage(Guid Id, Guid ThreadId, string SenderUserName, string Body, DateTimeOffset CreatedAtUtc, string? SenderDisplayName = null, string? SenderAddress = null);
public sealed record StudioExternalThread(Guid ConversationId,string ExternalAddress,string OwnerUserName,string Subject);
public sealed record StudioMessageUser(string UserName, string DisplayName);
public sealed record StartDirectMessageRequest(string UserName);
public sealed record StartExternalMessageRequest(string Address,string Subject);
public sealed record SendStudioMessageRequest(string Body);