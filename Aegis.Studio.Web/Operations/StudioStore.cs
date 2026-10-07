using System.Security.Claims;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Operations;

public sealed class StudioStore
{
    private readonly StudioDatabase database;
    private readonly WorkspaceStore workspaces;

    public StudioStore(StudioDatabase database, WorkspaceStore workspaces)
    {
        this.database = database;
        this.workspaces = workspaces;
    }

    public IReadOnlyList<StudioWorkspace> GetWorkspaces()
    {
        IReadOnlyList<StudioWorkspaceDefinition> definitions = workspaces.GetAll();
        using NpgsqlConnection connection = database.OpenConnection();
        List<StudioWorkspace> result = [];
        foreach (StudioWorkspaceDefinition workspace in definitions)
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select count(*) from studio_conversations where workspace_id = $1 and not is_closed";
            command.Parameters.AddWithValue(workspace.Id);
            int openCount = Convert.ToInt32(command.ExecuteScalar());
            result.Add(new StudioWorkspace(workspace.Id, workspace.Name, workspace.EmailAddress ?? string.Empty, openCount));
        }
        return result;
    }

    public IReadOnlyList<StudioConversation> GetConversations(Guid? workspaceId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select c.id, c.workspace_id, c.subject, c.contact_name, c.contact_address, c.channel,
                   c.is_closed, c.job_id, c.external_thread_id
            from studio_conversations c
            where not c.is_closed and ($1::uuid is null or c.workspace_id = $1)
            order by coalesce((select max(m.created_at_utc) from studio_messages m where m.conversation_id = c.id), c.created_at_utc) desc
            """;
        command.Parameters.AddWithValue((object?)workspaceId ?? DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioConversation> values = [];
        while (reader.Read())
        {
            values.Add(ReadConversation(reader));
        }
        return values;
    }

    public StudioConversation? GetConversation(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id, workspace_id, subject, contact_name, contact_address, channel, is_closed, job_id, external_thread_id
            from studio_conversations where id = $1
            """;
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public StudioJob? GetJob(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, workspace_id, conversation_id, title, client_name, status, event_date, created_at_utc, assigned_to, client_id, (select display_name from studio_clients where id=client_id),array(select workspace_id from studio_job_workspace_links where job_id=studio_jobs.id),job_lead from studio_jobs where id = $1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    public IReadOnlyList<StudioJob> GetJobs(Guid[]? allowedIds=null)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, workspace_id, conversation_id, title, client_name, status, event_date, created_at_utc, assigned_to, client_id, (select display_name from studio_clients where id=client_id),array(select workspace_id from studio_job_workspace_links where job_id=studio_jobs.id),job_lead from studio_jobs where ($1::uuid[] is null or id=any($1)) order by created_at_utc desc";
        command.Parameters.AddWithValue((object?)allowedIds??DBNull.Value);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioJob> values = [];
        while (reader.Read())
        {
            values.Add(ReadJob(reader,readFinancials:false));
        }
        reader.Close();
        var finances=new Dictionary<Guid,List<StudioFinancialEvent>>();var links=new Dictionary<Guid,List<Guid>>();
        using(var events=new NpgsqlCommand("select job_id,id,kind,amount,note,created_at_utc,payment_method,recorded_by from studio_financial_events where job_id=any($1) order by created_at_utc",connection))
        {
            events.Parameters.AddWithValue(values.Select(j=>j.Id).ToArray());using var rows=events.ExecuteReader();
            while(rows.Read()){Guid id=rows.GetGuid(0);if(!finances.TryGetValue(id,out var list))finances[id]=list=[];list.Add(new(rows.GetGuid(1),rows.GetString(2),rows.GetDecimal(3),rows.IsDBNull(4)?null:rows.GetString(4),rows.GetFieldValue<DateTimeOffset>(5),rows.IsDBNull(6)?null:rows.GetString(6),rows.IsDBNull(7)?null:rows.GetString(7)));}
        }
        using(var participation=new NpgsqlCommand("select job_id,workspace_id from studio_job_workspace_links where job_id=any($1)",connection))
        {
            participation.Parameters.AddWithValue(values.Select(j=>j.Id).ToArray());using var rows=participation.ExecuteReader();
            while(rows.Read()){Guid id=rows.GetGuid(0);if(!links.TryGetValue(id,out var list))links[id]=list=[];list.Add(rows.GetGuid(1));}
        }
        return values.Select(j=>j with{FinancialEvents=finances.GetValueOrDefault(j.Id,[]),ParticipatingWorkspaceIds=links.GetValueOrDefault(j.Id,[])}).ToArray();
    }

    public StudioConversation ReceiveInteraction(IncomingInteractionRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination);
        Guid conversationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        InsertConversation(connection, transaction, conversationId, workspaceId, request.Subject.Trim(), request.ContactName.Trim(), request.ContactAddress.Trim(), NormaliseChannel(request.Channel), null, now);
        InsertMessage(connection, transaction, new StudioMessage(Guid.NewGuid(), request.ContactName.Trim(), "External", request.Body.Trim(), now, null, null), conversationId);
        transaction.Commit();
        return GetConversation(conversationId)!;
    }

    public EmailIngestResult ReceiveEmail(InboundEmail email, EmailRouteResult route)
    {
        if (!route.IsRouted || !route.WorkspaceId.HasValue)
        {
            throw new InvalidOperationException("An email must be routed before it can be ingested.");
        }

        StudioConversation? duplicate = FindByExternalMessageId(email.MessageId);
        if (duplicate is not null)
        {
            return new EmailIngestResult(duplicate, false, true);
        }

        StudioConversation? existing = null;
        if (route.DestinationType.Equals("Job", StringComparison.OrdinalIgnoreCase) && route.DestinationId is Guid routedJobId)
        {
            StudioJob? routedJob = GetJob(routedJobId);
            existing = routedJob is null ? null : GetConversation(routedJob.ConversationId);
        }
        existing ??= FindConversationForEmail(email, route.WorkspaceId.Value);

        StudioMessage message = new(Guid.NewGuid(), email.FromName.Trim(), "External", email.Body.Trim(), email.ReceivedAt.ToUniversalTime(), email.MessageId, email.FromAddress.Trim());
        if (existing is not null)
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            if (string.IsNullOrWhiteSpace(existing.ExternalThreadId) && !string.IsNullOrWhiteSpace(email.ThreadId))
            {
                using NpgsqlCommand bindThread = connection.CreateCommand();
                bindThread.Transaction = transaction;
                bindThread.CommandText = "update studio_conversations set external_thread_id=$2 where id=$1 and external_thread_id is null";
                bindThread.Parameters.AddWithValue(existing.Id);
                bindThread.Parameters.AddWithValue(NormaliseThreadId(email.ThreadId)!);
                bindThread.ExecuteNonQuery();
            }
            InsertMessage(connection, transaction, message, existing.Id);
            transaction.Commit();
            return new EmailIngestResult(GetConversation(existing.Id)!, false, false);
        }

        Guid conversationId = Guid.NewGuid();
        using (NpgsqlConnection connection = database.OpenConnection())
        using (NpgsqlTransaction transaction = connection.BeginTransaction())
        {
            InsertConversation(connection, transaction, conversationId, route.WorkspaceId.Value, email.Subject.Trim(), email.FromName.Trim(), email.FromAddress.Trim(), "Email", NormaliseThreadId(email.ThreadId), DateTimeOffset.UtcNow);
            InsertMessage(connection, transaction, message, conversationId);
            transaction.Commit();
        }
        return new EmailIngestResult(GetConversation(conversationId)!, true, false);
    }

    public StudioConversation AddMessage(Guid id, AddMessageRequest request, ClaimsPrincipal user)
    {
        StudioConversation conversation = GetConversation(id) ?? throw new KeyNotFoundException("Conversation was not found.");
        string userName = user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "unknown";
        string displayName = user.Identity?.Name ?? userName;
        StudioMessage message = new(Guid.NewGuid(), displayName, request.IsInternal ? "Internal" : "External", request.Body.Trim(), DateTimeOffset.UtcNow, null, userName);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        InsertMessage(connection, transaction, message, conversation.Id);
        transaction.Commit();
        return GetConversation(id)!;
    }

    public StudioJob CreateJob(Guid conversationId, CreateJobRequest request)
    {
        StudioConversation conversation = GetConversation(conversationId) ?? throw new KeyNotFoundException("Conversation was not found.");
        if (conversation.JobId.HasValue)
        {
            StudioJob? existing = GetJob(conversation.JobId.Value);
            if (existing is not null)
            {
                return existing;
            }
        }

        Guid id = Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                insert into studio_jobs(id, workspace_id, conversation_id, title, client_name, status, event_date, created_at_utc, assigned_to, job_lead)
                values ($1,$2,$3,$4,$5,'New',$6,$7,null,(select default_job_lead from studio_workspaces where id=$2))
                """;
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(conversation.WorkspaceId);
            command.Parameters.AddWithValue(conversation.Id);
            command.Parameters.AddWithValue(request.Title.Trim());
            command.Parameters.AddWithValue(conversation.ContactName);
            command.Parameters.AddWithValue((object?)request.EventDate ?? DBNull.Value);
            command.Parameters.AddWithValue(DateTimeOffset.UtcNow);
            command.ExecuteNonQuery();
        }
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "update studio_conversations set job_id = $1 where id = $2";
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(conversation.Id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetJob(id)!;
    }

    public StudioJob CreateBookingJob(CreateBookingIntakeRequest request)
    {
        Guid workspaceId = ResolveWorkspace(request.WorkspaceId, request.Destination);
        string address = !string.IsNullOrWhiteSpace(request.ClientEmail) ? request.ClientEmail.Trim() : request.ClientPhone?.Trim() ?? "Walk-in";
        string note = string.IsNullOrWhiteSpace(request.Notes) ? $"Booking request for {request.SessionType}." : request.Notes.Trim();
        StudioConversation conversation = ReceiveInteraction(new IncomingInteractionRequest(workspaceId, null, request.ClientName, address, $"{request.SessionType} booking", note, request.Source));
        return CreateJob(conversation.Id, new CreateJobRequest(request.SessionType, DateOnly.FromDateTime(request.StartsAt.UtcDateTime)));
    }

    public StudioJob UpdateJob(Guid id, UpdateJobRequest request)
    {
        StudioJob current = GetJob(id) ?? throw new KeyNotFoundException("Job was not found.");
        string status = string.IsNullOrWhiteSpace(request.Status) ? current.Status : request.Status.Trim();
        if(status!=current.Status && current.Status is "Completed" or "Closed" or "Cancelled" or "Published")
            throw new ArgumentException("This job is finished and cannot move to another stage.");
        if(status!=current.Status && status is "Needs Review" or "Changes Requested" or "In Production" or "Ready for Client" or "Published")
            throw new ArgumentException("Use the job workflow action to advance review, production or publication.");
        if(status!=current.Status && current.Status is "Needs Review" or "Changes Requested" or "In Production" or "Ready for Client")
            throw new ArgumentException("Use the job workflow action to change the review or production stage.");
        string? assignedTo = string.IsNullOrWhiteSpace(request.AssignedTo) ? current.AssignedTo : request.AssignedTo.Trim();
        DateOnly? eventDate = request.EventDate ?? current.EventDate;
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_jobs set status=$2, assigned_to=$3, event_date=$4 where id=$1 and status=$5";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)assignedTo ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)eventDate ?? DBNull.Value);
        command.Parameters.AddWithValue(current.Status);
        if(command.ExecuteNonQuery()!=1)throw new ArgumentException("The job changed while saving. Refresh and try again.");
        return GetJob(id)!;
    }

    public StudioJob MoveJobWorkspace(Guid id, Guid workspaceId)
    {
        StudioJob current=GetJob(id)??throw new KeyNotFoundException("Job was not found.");
        if(current.WorkspaceId==workspaceId)return current;
        using NpgsqlConnection connection=database.OpenConnection();
        using NpgsqlTransaction transaction=connection.BeginTransaction();
        using(NpgsqlCommand job=connection.CreateCommand())
        {
            job.Transaction=transaction;
            job.CommandText="update studio_jobs set workspace_id=$2 where id=$1";
            job.Parameters.AddWithValue(id);job.Parameters.AddWithValue(workspaceId);
            if(job.ExecuteNonQuery()!=1)throw new KeyNotFoundException("Job was not found.");
        }
        using(NpgsqlCommand conversation=connection.CreateCommand())
        {
            conversation.Transaction=transaction;
            conversation.CommandText="update studio_conversations set workspace_id=$2 where id=$1";
            conversation.Parameters.AddWithValue(current.ConversationId);conversation.Parameters.AddWithValue(workspaceId);
            conversation.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetJob(id)!;
    }

    public StudioJob AddFinancialEvent(Guid id, AddFinancialEventRequest request,string? actor=null,Guid? eventId=null)
    {
        if (GetJob(id) is null)
        {
            throw new KeyNotFoundException("Job was not found.");
        }
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_financial_events(id, job_id, kind, amount, note, created_at_utc,payment_method,recorded_by) values ($1,$2,$3,$4,$5,$6,$7,$8) on conflict(id) do nothing";
        command.Parameters.AddWithValue(eventId??Guid.NewGuid());
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(request.Kind.Trim());
        command.Parameters.AddWithValue(request.Amount);
        command.Parameters.AddWithValue((object?)request.Note?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue((object?)request.PaymentMethod??DBNull.Value);
        command.Parameters.AddWithValue((object?)actor??DBNull.Value);
        command.ExecuteNonQuery();
        return GetJob(id)!;
    }

    public AccountsSummary GetAccountsSummary()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select coalesce((select sum(amount) from studio_financial_events),0),
                   (select count(distinct job_id) from studio_financial_events),
                   (select count(*) from studio_jobs)
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        reader.Read();
        return new AccountsSummary(reader.GetDecimal(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private Guid ResolveWorkspace(Guid? id, string? destination)
    {
        if (id.HasValue)
        {
            StudioWorkspaceDefinition? workspace = workspaces.Get(id.Value);
            if (workspace is null || !workspace.IsEnabled)
            {
                throw new InvalidOperationException("The selected workspace does not exist or is disabled.");
            }
            return workspace.Id;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new InvalidOperationException("WorkspaceId or an exact workspace route/address is required.");
        }

        StudioWorkspaceDefinition? byRoute = workspaces.FindByRouteKey(destination);
        StudioWorkspaceDefinition? byAddress = workspaces.FindByAddress(destination);
        StudioWorkspaceDefinition? resolved = byRoute ?? byAddress;
        return resolved?.Id ?? throw new InvalidOperationException("No workspace matches the supplied route/address. Heuristic routing is disabled.");
    }

    private StudioConversation? FindByExternalMessageId(string messageId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select conversation_id from studio_messages where external_message_id = $1 limit 1";
        command.Parameters.AddWithValue(messageId);
        object? value = command.ExecuteScalar();
        return value is Guid id ? GetConversation(id) : null;
    }

    private StudioConversation? FindConversationForEmail(InboundEmail email, Guid workspaceId)
    {
        string? threadId = NormaliseThreadId(email.ThreadId);
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        if (threadId is not null)
        {
            command.CommandText = "select id from studio_conversations where workspace_id=$1 and external_thread_id=$2 order by created_at_utc desc limit 1";
            command.Parameters.AddWithValue(workspaceId);
            command.Parameters.AddWithValue(threadId);
            object? threaded = command.ExecuteScalar();
            if (threaded is Guid threadedId)
            {
                return GetConversation(threadedId);
            }
            command.Parameters.Clear();
        }

        command.CommandText = "select id, subject from studio_conversations where workspace_id=$1 and lower(contact_address)=lower($2) order by created_at_utc desc";
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(email.FromAddress.Trim());
        using NpgsqlDataReader reader = command.ExecuteReader();
        string subject = NormaliseSubject(email.Subject);
        while (reader.Read())
        {
            if (string.Equals(NormaliseSubject(reader.GetString(1)), subject, StringComparison.OrdinalIgnoreCase))
            {
                Guid id = reader.GetGuid(0);
                reader.Close();
                return GetConversation(id);
            }
        }
        return null;
    }

    private StudioConversation ReadConversation(NpgsqlDataReader reader)
    {
        Guid id = reader.GetGuid(0);
        List<StudioMessage> messages = GetMessages(id);
        return new StudioConversation(id, reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetGuid(7), messages, reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private List<StudioMessage> GetMessages(Guid conversationId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, author_display_name, visibility, body, created_at_utc, external_message_id, author_user_name from studio_messages where conversation_id=$1 order by created_at_utc";
        command.Parameters.AddWithValue(conversationId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioMessage> values = [];
        while (reader.Read())
        {
            values.Add(new StudioMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return values;
    }

    private StudioJob ReadJob(NpgsqlDataReader reader,bool readFinancials=true)
    {
        Guid id = reader.GetGuid(0);
        return new StudioJob(id, reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : DateOnly.FromDateTime(reader.GetDateTime(6)), reader.GetFieldValue<DateTimeOffset>(7), reader.IsDBNull(8) ? null : reader.GetString(8), readFinancials?GetFinancialEvents(id):[], reader.IsDBNull(9)?null:reader.GetGuid(9), reader.IsDBNull(10)?null:reader.GetString(10),reader.FieldCount>11?reader.GetFieldValue<Guid[]>(11):null, reader.FieldCount>12 && !reader.IsDBNull(12) ? reader.GetString(12) : null);
    }

    private List<StudioFinancialEvent> GetFinancialEvents(Guid jobId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, kind, amount, note, created_at_utc,payment_method,recorded_by from studio_financial_events where job_id=$1 order by created_at_utc";
        command.Parameters.AddWithValue(jobId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioFinancialEvent> values = [];
        while (reader.Read())
        {
            values.Add(new StudioFinancialEvent(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6)));
        }
        return values;
    }

    private static void InsertConversation(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid workspaceId, string subject, string contactName, string contactAddress, string channel, string? externalThreadId, DateTimeOffset createdAt)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_conversations(id, workspace_id, subject, contact_name, contact_address, channel, is_closed, job_id, external_thread_id, created_at_utc) values ($1,$2,$3,$4,$5,$6,false,null,$7,$8)";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(workspaceId);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(contactName);
        command.Parameters.AddWithValue(contactAddress);
        command.Parameters.AddWithValue(channel);
        command.Parameters.AddWithValue((object?)externalThreadId ?? DBNull.Value);
        command.Parameters.AddWithValue(createdAt);
        command.ExecuteNonQuery();
    }

    private static void InsertMessage(NpgsqlConnection connection, NpgsqlTransaction transaction, StudioMessage message, Guid conversationId)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_messages(id, conversation_id, author_user_name, author_display_name, visibility, body, created_at_utc, external_message_id) values ($1,$2,$3,$4,$5,$6,$7,$8)";
        command.Parameters.AddWithValue(message.Id);
        command.Parameters.AddWithValue(conversationId);
        command.Parameters.AddWithValue((object?)message.AuthorUserName ?? DBNull.Value);
        command.Parameters.AddWithValue(message.Author);
        command.Parameters.AddWithValue(message.Visibility);
        command.Parameters.AddWithValue(message.Body);
        command.Parameters.AddWithValue(message.CreatedAt);
        command.Parameters.AddWithValue((object?)message.ExternalMessageId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string NormaliseChannel(string? channel) => string.IsNullOrWhiteSpace(channel) ? "Manual" : channel.Trim();
    private static string? NormaliseThreadId(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    private static string NormaliseSubject(string subject) { string value = subject.Trim(); while (value.StartsWith("RE:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FW:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("FWD:", StringComparison.OrdinalIgnoreCase)) { value = value[(value.IndexOf(':') + 1)..].Trim(); } return value; }
}

public sealed record StudioWorkspace(Guid Id, string Name, string Address, int OpenCount);
public sealed record StudioConversation(Guid Id, Guid WorkspaceId, string Subject, string ContactName, string ContactAddress, string Channel, bool IsClosed, Guid? JobId, List<StudioMessage> Messages, string? ExternalThreadId = null);
public sealed record StudioMessage(Guid Id, string Author, string Visibility, string Body, DateTimeOffset CreatedAt, string? ExternalMessageId = null, string? AuthorUserName = null);
public sealed record StudioJob(Guid Id, Guid WorkspaceId, Guid ConversationId, string Title, string ClientName, string Status, DateOnly? EventDate, DateTimeOffset CreatedAt, string? AssignedTo = null, List<StudioFinancialEvent>? FinancialEvents = null, Guid? ClientId = null, string? LinkedClientName = null,IReadOnlyList<Guid>? ParticipatingWorkspaceIds=null,string? JobLead=null);
public sealed record StudioFinancialEvent(Guid Id, string Kind, decimal Amount, string? Note, DateTimeOffset CreatedAt,string? PaymentMethod=null,string? RecordedBy=null);
public sealed record EmailIngestResult(StudioConversation Conversation, bool CreatedConversation, bool WasDuplicate);
public sealed record IncomingInteractionRequest(Guid? WorkspaceId, string? Destination, string ContactName, string ContactAddress, string Subject, string Body, string? Channel);
public sealed record AddMessageRequest(string Body, bool IsInternal, string? SendingIdentity = null);
public sealed record CreateJobRequest(string Title, DateOnly? EventDate);
public sealed record UpdateJobRequest(string? Status, string? AssignedTo, DateOnly? EventDate);
public sealed record PipelineJobTransitionRequest(string? Status, Guid? WorkspaceId, string? Reason);
public sealed record AddFinancialEventRequest(string Kind, decimal Amount, string? Note,string? PaymentMethod=null);
public sealed record AccountsSummary(decimal RecordedAmount, int JobsWithFinancialActivity, int TotalJobs);

