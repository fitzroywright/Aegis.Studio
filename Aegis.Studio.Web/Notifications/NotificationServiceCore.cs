using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class NotificationService
{
    private readonly StudioDatabase database;
    private readonly UserProfileStore profiles;
    private readonly StudioStore studio;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;
    private readonly StudioOperationsFlowPublisher operationsFlows;

    public NotificationService(
        StudioDatabase database,
        UserProfileStore profiles,
        StudioStore studio,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess,
        StudioOperationsFlowPublisher operationsFlows)
    {
        this.database = database;
        this.profiles = profiles;
        this.studio = studio;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
        this.operationsFlows = operationsFlows;
    }

    public void PublishToAll(string eventType, string subject, string body, string? referenceType = null, string? referenceId = null)
    {
        IReadOnlyList<string>? scopedRecipients = ResolveScopedRecipients(referenceType, referenceId);
        if (scopedRecipients is not null)
        {
            foreach (string userName in scopedRecipients) PublishToUser(userName, eventType, subject, body, referenceType, referenceId);
            return;
        }

        foreach (StudioUserProfile profile in profiles.GetAll())
        {
            if (!WantsEvent(profile.Messaging, eventType)) continue;
            QueueForProfile(profile, eventType, subject, body, referenceType, referenceId);
        }
    }

    public void PublishToUser(string userName, string eventType, string subject, string body, string? referenceType = null, string? referenceId = null)
    {
        StudioUserProfile? profile = profiles.GetAll().FirstOrDefault(x =>
            string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.DisplayName, userName, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !WantsEvent(profile.Messaging, eventType)) return;
        QueueForProfile(profile, eventType, subject, body, referenceType, referenceId);
    }

    public IReadOnlyList<StudioNotification> GetForUser(string userName, int count = 100)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,attempts,last_error,created_at_utc,delivered_at_utc
            from studio_notifications
            where lower(user_name)=lower($1)
            order by created_at_utc desc
            limit $2
            """;
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(Math.Clamp(count, 1, 500));
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<StudioNotification> values = [];
        while (reader.Read()) values.Add(Read(reader));
        return values;
    }

    public void MarkRead(Guid id, string userName)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_notifications set is_read=true where id=$1 and lower(user_name)=lower($2) and channel='InApp'";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userName);
        command.ExecuteNonQuery();
        return id;
    }

    private IReadOnlyList<string>? ResolveScopedRecipients(string? referenceType, string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId)) return null;
        HashSet<string> recipients = new(StringComparer.OrdinalIgnoreCase);

        if (referenceType.Equals("Job", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(referenceId, out Guid jobId))
        {
            AddJobRecipients(jobId, recipients);
            return recipients.ToList();
        }
        if (referenceType.Equals("Conversation", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(referenceId, out Guid conversationId))
        {
            StudioConversation? conversation = studio.GetConversation(conversationId);
            if (conversation is null) return [];
            if (conversation.JobId.HasValue) AddJobRecipients(conversation.JobId.Value, recipients);
            else AddWorkspaceRecipients(conversation.WorkspaceId, recipients);
            return recipients.ToList();
        }
        if (referenceType.Equals("Workspace", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(referenceId, out Guid workspaceId))
        {
            AddWorkspaceRecipients(workspaceId, recipients);
            return recipients.ToList();
        }
        if (referenceType.Equals("Booking", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(referenceId, out Guid bookingId))
        {
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "select job_id from studio_bookings where id=$1";
            command.Parameters.AddWithValue(bookingId);
            object? value = command.ExecuteScalar();
            if (value is Guid bookingJobId) AddJobRecipients(bookingJobId, recipients);
            return recipients.ToList();
        }
        return null;
    }

    private void AddJobRecipients(Guid jobId, HashSet<string> recipients)
    {
        StudioJob? job = studio.GetJob(jobId);
        if (job is null) return;
        foreach (JobMember member in jobAccess.GetMembers(jobId).Where(x => x.IsActive)) recipients.Add(member.UserName);
        AddWorkspaceRecipients(job.WorkspaceId, recipients);
    }

    private void AddWorkspaceRecipients(Guid workspaceId, HashSet<string> recipients)
    {
        foreach (WorkspaceMember member in workspaceAccess.GetMembers(workspaceId).Where(x => x.IsActive)) recipients.Add(member.UserName);
    }

    private void QueueForProfile(StudioUserProfile profile, string eventType, string subject, string body, string? referenceType, string? referenceId)
    {
        UserMessagingPreferences preferences = profile.Messaging;
        if (preferences.InAppEnabled)
        {
            Guid id = Insert(profile.UserName, "InApp", eventType, subject, body, referenceType, referenceId, "Delivered", null);
            operationsFlows.TryEnqueueNotification(
                string.IsNullOrWhiteSpace(referenceId) ? id.ToString("D") : referenceId,
                "InApp",
                businessId: id.ToString("D"));
        }
        if (preferences.EmailEnabled && !string.IsNullOrWhiteSpace(profile.Email)) Insert(profile.UserName, "Email", eventType, subject, body, referenceType, referenceId, "Pending", profile.Email);
        if (preferences.SlackEnabled) Insert(profile.UserName, "Slack", eventType, subject, body, referenceType, referenceId, "Pending", null);
        if (preferences.TeamsEnabled) Insert(profile.UserName, "Teams", eventType, subject, body, referenceType, referenceId, "Pending", null);
    }

    private Guid Insert(string userName, string channel, string eventType, string subject, string body, string? referenceType, string? referenceId, string status, string? destination)
    {
        Guid id = Guid.NewGuid();
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_notifications
            (id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,destination,attempts,last_error,is_read,created_at_utc,delivered_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,0,null,false,now(),case when $9='Delivered' then now() else null end)
            """;
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userName);
        command.Parameters.AddWithValue(channel);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(body);
        command.Parameters.AddWithValue((object?)referenceType ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)referenceId ?? DBNull.Value);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)destination ?? DBNull.Value);
        command.ExecuteNonQuery();
        return id;
    }

    private static bool WantsEvent(UserMessagingPreferences preferences, string eventType) => eventType switch
    {
        "WorkspaceMessage" => preferences.NewWorkspaceMessage,
        "JobAssigned" => preferences.JobAssigned,
        "BookingChanged" => preferences.BookingChanged,
        "PaymentChanged" => preferences.PaymentChanged,
        "JobStatusChanged" => preferences.JobStatusChanged,
        "Mention" => preferences.MentionsAndReplies,
        _ => true
    };

    private static StudioNotification Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
        reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<DateTimeOffset>(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
}

public sealed record StudioNotification(Guid Id, string UserName, string Channel, string EventType, string Subject, string Body, string? ReferenceType, string? ReferenceId, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc);
internal sealed record PendingNotification(
    Guid Id,
    string UserName,
    string Channel,
    string Subject,
    string Body,
    string? Destination,
    int Attempts,
    string? ReferenceType,
    string? ReferenceId);
