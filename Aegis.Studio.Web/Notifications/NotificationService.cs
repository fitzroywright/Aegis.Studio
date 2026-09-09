using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Messaging;
using Common.Secrets;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class NotificationService
{
    private readonly StudioDatabase database;
    private readonly UserProfileStore profiles;
    private readonly StudioStore studio;
    private readonly WorkspaceAccessService workspaceAccess;
    private readonly JobAccessService jobAccess;

    public NotificationService(
        StudioDatabase database,
        UserProfileStore profiles,
        StudioStore studio,
        WorkspaceAccessService workspaceAccess,
        JobAccessService jobAccess)
    {
        this.database = database;
        this.profiles = profiles;
        this.studio = studio;
        this.workspaceAccess = workspaceAccess;
        this.jobAccess = jobAccess;
    }

    public void PublishToAll(string eventType, string subject, string body, string? referenceType = null, string? referenceId = null)
    {
        IReadOnlyList<string>? scopedRecipients = ResolveScopedRecipients(referenceType, referenceId);
        if (scopedRecipients is not null)
        {
            foreach (string userName in scopedRecipients)
            {
                PublishToUser(userName, eventType, subject, body, referenceType, referenceId);
            }
            return;
        }

        foreach (StudioUserProfile profile in profiles.GetAll())
        {
            if (!WantsEvent(profile.Messaging, eventType))
            {
                continue;
            }

            QueueForProfile(profile, eventType, subject, body, referenceType, referenceId);
        }
    }

    public void PublishToUser(string userName, string eventType, string subject, string body, string? referenceType = null, string? referenceId = null)
    {
        StudioUserProfile? profile = profiles.GetAll().FirstOrDefault(x =>
            string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.DisplayName, userName, StringComparison.OrdinalIgnoreCase));
        if (profile is null || !WantsEvent(profile.Messaging, eventType))
        {
            return;
        }

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
        while (reader.Read())
        {
            values.Add(Read(reader));
        }
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
    }

    private IReadOnlyList<string>? ResolveScopedRecipients(string? referenceType, string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceType) || string.IsNullOrWhiteSpace(referenceId))
        {
            return null;
        }

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
            Insert(profile.UserName, "InApp", eventType, subject, body, referenceType, referenceId, "Delivered", null);
        }
        if (preferences.EmailEnabled && !string.IsNullOrWhiteSpace(profile.Email))
        {
            Insert(profile.UserName, "Email", eventType, subject, body, referenceType, referenceId, "Pending", profile.Email);
        }
        if (preferences.SlackEnabled)
        {
            Insert(profile.UserName, "Slack", eventType, subject, body, referenceType, referenceId, "Pending", null);
        }
        if (preferences.TeamsEnabled)
        {
            Insert(profile.UserName, "Teams", eventType, subject, body, referenceType, referenceId, "Pending", null);
        }
    }

    private void Insert(string userName, string channel, string eventType, string subject, string body, string? referenceType, string? referenceId, string status, string? destination)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_notifications
            (id,user_name,channel,event_type,subject,body,reference_type,reference_id,status,destination,attempts,last_error,is_read,created_at_utc,delivered_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,0,null,false,now(),case when $9='Delivered' then now() else null end)
            """;
        command.Parameters.AddWithValue(Guid.NewGuid());
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
    }

    private static bool WantsEvent(UserMessagingPreferences preferences, string eventType)
        => eventType switch
        {
            "WorkspaceMessage" => preferences.NewWorkspaceMessage,
            "JobAssigned" => preferences.JobAssigned,
            "BookingChanged" => preferences.BookingChanged,
            "PaymentChanged" => preferences.PaymentChanged,
            "JobStatusChanged" => preferences.JobStatusChanged,
            "Mention" => preferences.MentionsAndReplies,
            _ => true
        };

    private static StudioNotification Read(NpgsqlDataReader reader)
        => new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12));
}

public sealed class NotificationDeliveryWorker : BackgroundService, IAsyncDisposable
{
    private const string QueueName = "aegis-studio";
    private readonly StudioDatabase database;
    private readonly StudioMessageRecipientDirectory recipientDirectory;
    private readonly PostgreSqlExternalDeliveryStore deliveryStore;
    private readonly ExternalDeliveryProcessor processor;
    private readonly IExternalMessageChannel[] externalChannels;
    private readonly ILogger<NotificationDeliveryWorker> logger;

    public NotificationDeliveryWorker(
        StudioDatabase database,
        MicrosoftGraphEmailClient graph,
        MicrosoftGraphEmailOptions graphOptions,
        IConfiguration configuration,
        UserProfileStore profiles,
        ILogger<NotificationDeliveryWorker> logger)
    {
        this.database = database;
        this.logger = logger;
        this.recipientDirectory = new StudioMessageRecipientDirectory(profiles);

        string connectionString = configuration.GetConnectionString("Studio")
            ?? throw new InvalidOperationException("Studio PostgreSQL connection string is not configured for durable messaging.");
        ExternalDeliveryOptions deliveryOptions = new()
        {
            DeliveryMode = ExternalDeliveryMode.ParallelByRecipient,
            MaxConcurrency = 4,
            MaxAttempts = 10,
            InitialRetryDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromMinutes(5),
            LeaseDuration = TimeSpan.FromMinutes(2)
        };

        ISecretProvider secrets = CommonSecretProviderFactory.Create(configuration);
        this.externalChannels =
        [
            new StudioGraphEmailChannel(graph, graphOptions),
            new StudioWebhookMessageChannel(
                MessageChannel.Slack,
                secrets,
                "Notifications:SlackWebhookUrl",
                request => new { text = $"*{request.Title}*\n{request.Body}" }),
            new StudioWebhookMessageChannel(
                MessageChannel.MsTeams,
                secrets,
                "Notifications:TeamsWebhookUrl",
                request => new { text = $"**{request.Title}**\n\n{request.Body}" })
        ];

        this.deliveryStore = new PostgreSqlExternalDeliveryStore(connectionString, deliveryOptions, QueueName);
        ExternalDeliveryDispatcher dispatcher = new(
            this.externalChannels,
            deliveryOptions,
            idempotencyStore: this.deliveryStore);
        this.processor = new ExternalDeliveryProcessor(this.deliveryStore, dispatcher, deliveryOptions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task processorTask = this.processor.RunAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ReconcileDeliveryState();
                    foreach (PendingNotification item in GetPending())
                    {
                        await QueueAsync(item, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    this.logger.LogError(exception, "Notification queueing cycle failed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        finally
        {
            try
            {
                await processorTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private IReadOnlyList<PendingNotification> GetPending()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,user_name,channel,subject,body,destination,attempts
            from studio_notifications
            where status in ('Pending','Retry','WaitingForConfiguration')
              and attempts < 10
            order by created_at_utc
            limit 50
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PendingNotification> values = [];
        while (reader.Read())
        {
            values.Add(new PendingNotification(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6)));
        }
        return values;
    }

    private async Task QueueAsync(PendingNotification item, CancellationToken cancellationToken)
    {
        MessageChannel channel = item.Channel switch
        {
            "Email" => MessageChannel.MsEmail,
            "Slack" => MessageChannel.Slack,
            "Teams" => MessageChannel.MsTeams,
            _ => MessageChannel.None
        };

        if (channel == MessageChannel.None)
        {
            MarkDelivered(item.Id);
            return;
        }

        IReadOnlyList<MessageRecipient> resolved = await this.recipientDirectory.ResolveAsync([item.UserName], cancellationToken);
        MessageRecipient? recipient = resolved.FirstOrDefault();
        if (recipient is null)
        {
            MarkRetry(item.Id, "Recipient profile could not be resolved.");
            return;
        }

        Dictionary<string, string>? metadata = string.IsNullOrWhiteSpace(item.Destination)
            ? null
            : new Dictionary<string, string> { ["Destination"] = item.Destination };
        MessageRequest request = new()
        {
            RecipientIds = [item.UserName],
            Title = item.Subject,
            Body = item.Body,
            Channels = channel,
            Source = "Aegis.Studio",
            CorrelationId = item.Id.ToString("D"),
            Metadata = metadata
        };
        RecipientSnapshot snapshot = new(
            recipient.UserId,
            recipient.UserName,
            recipient.Email,
            recipient.SlackUserId,
            recipient.Mobile,
            recipient.PreferredChannels);
        ExternalDeliveryWorkItem workItem = new(
            item.Id,
            [snapshot],
            channel,
            request,
            item.Id.ToString("D"),
            "Aegis.Studio notification",
            DateTimeOffset.UtcNow,
            item.Attempts);

        await this.deliveryStore.EnqueueAsync(workItem, cancellationToken);
        Update(item.Id, "Queued", null, false, false);
    }

    private void ReconcileDeliveryState()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            update studio_notifications n
            set status = case
                    when q.state='completed' then 'Delivered'
                    when q.state='dead' then 'Failed'
                    else n.status
                end,
                attempts = greatest(n.attempts, q.attempt),
                last_error = case when q.state='dead' then q.error_code else n.last_error end,
                delivered_at_utc = case when q.state='completed' then q.updated_at else n.delivered_at_utc end
            from common_messaging_queue q
            where q.queue_name=$1
              and q.notification_id=n.id
              and n.status in ('Queued','Retry','WaitingForConfiguration')
            """;
        command.Parameters.AddWithValue(QueueName);
        command.ExecuteNonQuery();
    }

    private void MarkDelivered(Guid id) => Update(id, "Delivered", null, true, true);
    private void MarkRetry(Guid id, string error) => Update(id, "Retry", error, false, true);

    private void Update(Guid id, string status, string? error, bool delivered, bool incrementAttempts)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_notifications set status=$2, attempts=attempts+case when $5 then 1 else 0 end, last_error=$3, delivered_at_utc=case when $4 then now() else delivered_at_utc end where id=$1";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(delivered);
        command.Parameters.AddWithValue(incrementAttempts);
        command.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable disposable in this.externalChannels.OfType<IDisposable>())
        {
            disposable.Dispose();
        }
        await this.deliveryStore.DisposeAsync();
        base.Dispose();
    }
}

public sealed record StudioNotification(Guid Id, string UserName, string Channel, string EventType, string Subject, string Body, string? ReferenceType, string? ReferenceId, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc);
internal sealed record PendingNotification(Guid Id, string UserName, string Channel, string Subject, string Body, string? Destination, int Attempts);
