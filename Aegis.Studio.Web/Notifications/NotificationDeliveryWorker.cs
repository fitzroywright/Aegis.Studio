using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Messaging;
using Common.Messaging.Channels.MicrosoftGraph;
using Common.Messaging.Channels.Slack;
using Common.Messaging.Channels.Teams;
using Common.Secrets;
using Npgsql;

namespace Aegis.Studio.Web.Notifications;

public sealed class NotificationDeliveryWorker : BackgroundService, IAsyncDisposable
{
    private const string QueueName = "aegis-studio";
    private readonly StudioDatabase database;
    private readonly StudioMessageRecipientDirectory recipientDirectory;
    private readonly PostgreSqlExternalDeliveryStore deliveryStore;
    private readonly ExternalDeliveryProcessor processor;
    private readonly IExternalMessageChannel[] externalChannels;
    private readonly HttpClient[] ownedHttpClients;
    private readonly ILogger<NotificationDeliveryWorker> logger;

    public NotificationDeliveryWorker(
        StudioDatabase database,
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
        IChannelSecretResolver secretResolver = new StudioCommonSecretsChannelSecretResolver(secrets);
        List<IExternalMessageChannel> channels = [];
        List<HttpClient> clients = [];

        if (configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false))
        {
            HttpClient client = new();
            clients.Add(client);
            channels.Add(new MicrosoftGraphEmailMessageChannel(
                client,
                new MicrosoftGraphEmailOptions
                {
                    TenantId = configuration["Messaging:MicrosoftGraph:TenantId"] ?? string.Empty,
                    ClientId = configuration["Messaging:MicrosoftGraph:ClientId"] ?? string.Empty,
                    SenderUpn = configuration["Messaging:MicrosoftGraph:Email:SenderUpn"] ?? string.Empty,
                    ClientSecretName = configuration["Messaging:MicrosoftGraph:ClientSecretName"] ?? "messaging/msgraph/client-secret"
                },
                secretResolver));
        }

        if (configuration.GetValue("Messaging:Slack:Enabled", false))
        {
            HttpClient client = new();
            clients.Add(client);
            channels.Add(new SlackMessageChannel(
                client,
                new SlackMessageOptions
                {
                    BotTokenSecretName = configuration["Messaging:Slack:BotTokenSecretName"] ?? "messaging/slack/bot-token"
                },
                secretResolver));
        }

        if (configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false))
        {
            HttpClient client = new();
            clients.Add(client);
            channels.Add(new MicrosoftGraphTeamsMessageChannel(
                client,
                new MicrosoftGraphTeamsOptions
                {
                    SenderUpn = configuration["Messaging:MicrosoftGraph:Teams:SenderUpn"] ?? string.Empty,
                    DelegatedAccessTokenSecretName = configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"]
                        ?? "messaging/msgraph/teams/delegated-access-token"
                },
                secretResolver));
        }
        else if (configuration.GetValue("Messaging:Teams:Enabled", false))
        {
            HttpClient client = new();
            clients.Add(client);
            channels.Add(new TeamsMessageChannel(
                client,
                new TeamsMessageOptions
                {
                    WebhookSecretName = configuration["Messaging:Teams:WebhookSecretName"] ?? "messaging/teams/webhook-url"
                },
                secretResolver));
        }

        this.externalChannels = channels.ToArray();
        this.ownedHttpClients = clients.ToArray();
        this.deliveryStore = new PostgreSqlExternalDeliveryStore(connectionString, deliveryOptions, QueueName);
        ExternalDeliveryDispatcher dispatcher = new(this.externalChannels, deliveryOptions, idempotencyStore: this.deliveryStore);
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
                    foreach (PendingNotification item in GetPending()) await QueueAsync(item, stoppingToken);
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
            try { await processorTask; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private IReadOnlyList<PendingNotification> GetPending()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            select id,user_name,channel,subject,body,destination,attempts,reference_type,reference_id
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
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
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

        if (!this.externalChannels.Any(x => x.Channel == channel))
        {
            Update(item.Id, "WaitingForConfiguration", $"Common.Messaging channel {channel} is disabled or unconfigured.", false, false);
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
        string correlationId =
            !string.IsNullOrWhiteSpace(item.ReferenceId)
                ? item.ReferenceId.Trim()
                : item.Id.ToString("D");

        if (!string.IsNullOrWhiteSpace(item.ReferenceType))
        {
            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata["ReferenceType"] = item.ReferenceType.Trim();
        }
        if (!string.IsNullOrWhiteSpace(item.ReferenceId))
        {
            metadata ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            metadata["ReferenceId"] = item.ReferenceId.Trim();
        }

        MessageRequest request = new()
        {
            RecipientIds = [item.UserName],
            Title = item.Subject,
            Body = item.Body,
            Channels = channel,
            Source = "Aegis.Studio",
            CorrelationId = correlationId,
            Metadata = metadata
        };
        RecipientSnapshot snapshot = new(recipient.UserId, recipient.UserName, recipient.Email, recipient.SlackUserId, recipient.Mobile, recipient.PreferredChannels);
        ExternalDeliveryWorkItem workItem = new(item.Id, [snapshot], channel, request, correlationId, "Aegis.Studio notification", DateTimeOffset.UtcNow, item.Attempts);

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
                    when q.state='completed' then 'ProviderAccepted'
                    when q.state='dead' then 'Failed'
                    else n.status
                end,
                attempts = greatest(n.attempts, q.attempt),
                last_error = case when q.state='dead' then q.error_code else n.last_error end,
                delivered_at_utc = case when q.state='delivered' then q.updated_at else n.delivered_at_utc end
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
        foreach (IDisposable disposable in this.externalChannels.OfType<IDisposable>()) disposable.Dispose();
        foreach (HttpClient client in this.ownedHttpClients) client.Dispose();
        await this.deliveryStore.DisposeAsync();
        base.Dispose();
    }
}

internal sealed class StudioCommonSecretsChannelSecretResolver(ISecretProvider secrets) : IChannelSecretResolver
{
    public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
        => secrets.GetAsync(secretName, cancellationToken);
}
