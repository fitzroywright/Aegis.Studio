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

    public StudioInAppMessageStore(
        StudioDatabase database,
        UserProfileStore profiles,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess,
        StudioStore studio)
    {
        this.database = database;
        this.profiles = profiles;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
        this.studio = studio;
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
               or (t.scope_type='Workspace' and t.scope_id=any($2))
               or (t.scope_type='Job' and t.scope_id=any($3))
            group by t.id,t.scope_type,t.scope_id,t.title,t.created_by,t.created_at_utc,r.last_read_at_utc
            order by coalesce(max(m.created_at_utc),t.created_at_utc) desc
            """;
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(workspaceAccess.GetAccessibleWorkspaceIds(user).Where(id=>workspaceAccess.HasPermission(user,id,"View")).ToArray());
        command.Parameters.AddWithValue(jobAccess.GetPermissionMap(user).Where(p=>p.Value.ViewJob).Select(p=>p.Key).ToArray());
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
        StudioUnifiedMessaging.LockRequest(connection,transaction,key);
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
        insert.CommandText = "insert into studio_message_threads(id,scope_type,scope_id,direct_key,title,created_by,created_at_utc) values($1,$2,$3,null,$4,$5,$6) on conflict(scope_type,scope_id) where scope_id is not null do update set scope_type=excluded.scope_type returning id";
        insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(normalized); insert.Parameters.AddWithValue(scopeId); insert.Parameters.AddWithValue(title); insert.Parameters.AddWithValue(current); insert.Parameters.AddWithValue(now);
        id=(Guid)insert.ExecuteScalar()!;
        return new StudioMessageThread(id, normalized, scopeId, title, current, now);
    }

    public IReadOnlyList<StudioInAppMessage> GetMessages(Guid threadId, ClaimsPrincipal user, int count = 100)
    {
        AssertThreadAccess(threadId, user);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id,thread_id,sender_user_name,body,created_at_utc,subject,recipient_addresses,internet_message_id from studio_inapp_messages where thread_id=$1 order by created_at_utc desc limit $2";
        command.Parameters.AddWithValue(threadId); command.Parameters.AddWithValue(Math.Clamp(count, 1, 500));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioInAppMessage> values = [];
        while (reader.Read()) values.Add(new StudioInAppMessage(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5), reader.GetFieldValue<string[]>(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
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
        if (scopeType == "Direct")
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
        if (scopeType == "Direct")
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
                    notifications.PublishInternalToUser(
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
public sealed record StudioInAppMessage(Guid Id, Guid ThreadId, string SenderUserName, string Body, DateTimeOffset CreatedAtUtc, string Subject = "", string[]? RecipientAddresses = null, string? InternetMessageId = null);
public sealed record StudioMessageUser(string UserName, string DisplayName);
public sealed record StartDirectMessageRequest(string UserName);
public sealed record SendStudioMessageRequest(string Body);
