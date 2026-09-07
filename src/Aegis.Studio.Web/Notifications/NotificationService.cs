using System.Net.Http.Json;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class NotificationService
{
    private readonly StudioDatabase database;
    private readonly UserProfileStore profiles;

    public NotificationService(StudioDatabase database, UserProfileStore profiles)
    {
        this.database = database;
        this.profiles = profiles;
    }

    public void PublishToAll(string eventType, string subject, string body, string? referenceType = null, string? referenceId = null)
    {
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

public sealed class NotificationDeliveryWorker : BackgroundService
{
    private readonly StudioDatabase database;
    private readonly MicrosoftGraphEmailClient graph;
    private readonly MicrosoftGraphEmailOptions graphOptions;
    private readonly IConfiguration configuration;
    private readonly ILogger<NotificationDeliveryWorker> logger;
    private readonly HttpClient httpClient = new();

    public NotificationDeliveryWorker(StudioDatabase database, MicrosoftGraphEmailClient graph, MicrosoftGraphEmailOptions graphOptions, IConfiguration configuration, ILogger<NotificationDeliveryWorker> logger)
    {
        this.database = database;
        this.graph = graph;
        this.graphOptions = graphOptions;
        this.configuration = configuration;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (PendingNotification item in GetPending())
                {
                    await DeliverAsync(item, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Notification delivery cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private IReadOnlyList<PendingNotification> GetPending()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,user_name,channel,subject,body,destination,attempts
            from studio_notifications
            where status in ('Pending','Retry') and attempts < 10
            order by created_at_utc
            limit 50
            """;
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<PendingNotification> values = [];
        while (reader.Read())
        {
            values.Add(new PendingNotification(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6)));
        }
        return values;
    }

    private async Task DeliverAsync(PendingNotification item, CancellationToken cancellationToken)
    {
        try
        {
            switch (item.Channel)
            {
                case "Email":
                    if (!graph.IsConfigured || string.IsNullOrWhiteSpace(item.Destination))
                    {
                        MarkWaitingForConfiguration(item.Id, "Microsoft Graph client secret or recipient email is not configured.");
                        return;
                    }
                    await graph.SendEmailAsync(graphOptions.Mailbox, item.Destination, item.Subject, item.Body, cancellationToken);
                    break;
                case "Slack":
                    string? slackWebhook = configuration["Notifications:SlackWebhookUrl"];
                    if (string.IsNullOrWhiteSpace(slackWebhook))
                    {
                        MarkWaitingForConfiguration(item.Id, "Slack webhook token is not configured.");
                        return;
                    }
                    using (HttpResponseMessage response = await httpClient.PostAsJsonAsync(slackWebhook, new { text = $"*{item.Subject}*\n{item.Body}" }, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                case "Teams":
                    string? teamsWebhook = configuration["Notifications:TeamsWebhookUrl"];
                    if (string.IsNullOrWhiteSpace(teamsWebhook))
                    {
                        MarkWaitingForConfiguration(item.Id, "Teams webhook token is not configured.");
                        return;
                    }
                    using (HttpResponseMessage response = await httpClient.PostAsJsonAsync(teamsWebhook, new { text = $"**{item.Subject}**\n\n{item.Body}" }, cancellationToken))
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    break;
                default:
                    MarkDelivered(item.Id);
                    return;
            }

            MarkDelivered(item.Id);
        }
        catch (Exception exception)
        {
            MarkRetry(item.Id, exception.Message);
        }
    }

    private void MarkDelivered(Guid id) => Update(id, "Delivered", null, true);
    private void MarkRetry(Guid id, string error) => Update(id, "Retry", error, false);
    private void MarkWaitingForConfiguration(Guid id, string error) => Update(id, "WaitingForConfiguration", error, false);

    private void Update(Guid id, string status, string? error, bool delivered)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_notifications set status=$2, attempts=attempts+1, last_error=$3, delivered_at_utc=case when $4 then now() else delivered_at_utc end where id=$1";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(status);
        command.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue(delivered);
        command.ExecuteNonQuery();
    }
}

public sealed record StudioNotification(Guid Id, string UserName, string Channel, string EventType, string Subject, string Body, string? ReferenceType, string? ReferenceId, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAtUtc, DateTimeOffset? DeliveredAtUtc);
internal sealed record PendingNotification(Guid Id, string UserName, string Channel, string Subject, string Body, string? Destination, int Attempts);
