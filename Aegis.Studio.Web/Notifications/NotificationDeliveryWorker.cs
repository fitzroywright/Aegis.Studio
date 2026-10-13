using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Profiles;
using Common.Messaging;
using Common.Messaging.Channels.MicrosoftGraph;
using Common.Messaging.Channels.Slack;
using Common.Messaging.Channels.Teams;
using Common.Messaging.Channels.WhatsApp;
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
    private readonly StudioOperationsFlowPublisher operationsFlows;
    private readonly ILogger<NotificationDeliveryWorker> logger;

    public NotificationDeliveryWorker(
        StudioDatabase database,
        IConfiguration configuration,
        UserProfileStore profiles,
        StudioOperationsFlowPublisher operationsFlows,
        ILogger<NotificationDeliveryWorker> logger)
    {
        this.database = database;
        this.operationsFlows = operationsFlows;
        this.logger = logger;
        this.recipientDirectory = new StudioMessageRecipientDirectory(profiles);

        string connectionString = database.ConnectionString;

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

        if (configuration.GetValue("Messaging:WhatsApp:Enabled", false))
        {
            HttpClient client = new();
            clients.Add(client);
            channels.Add(new WhatsAppMessageChannel(
                client,
                new WhatsAppMessageOptions
                {
                    Enabled = true,
                    GraphApiVersion = configuration["Messaging:WhatsApp:GraphApiVersion"] ?? "v23.0",
                    PhoneNumberId = configuration["Messaging:WhatsApp:PhoneNumberId"] ?? string.Empty,
                    AccessTokenSecretName = configuration["Messaging:WhatsApp:AccessTokenSecretName"] ?? "messaging/whatsapp/access-token"
                },
                secretResolver));
        }

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
        this.processor = new ExternalDeliveryProcessor(this.deliveryStore, new Aegis.Studio.Web.Operations.DemoSafeDeliveryDispatcher(database, dispatcher), deliveryOptions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task processorTask = RunProcessor(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var operation = Aegis.Studio.Web.Operations.DemoMaintenance.TryEnterOperation(database);
                    if (operation is null) { await Task.Delay(1000, stoppingToken); continue; }
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

    private async Task RunProcessor(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await processor.RunAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Notification processor failed; Studio remains available. Clear invalid demo data before recreating it."); }
            await Task.Delay(TimeSpan.FromSeconds(10), token);
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

    private bool IsWorkflowEmailStillAllowed(PendingNotification item)
    {
        using var connection=database.OpenConnection();
        using var cmd=connection.CreateCommand();
        cmd.CommandText="""
            select not exists(select 1 from studio_employees e where lower(e.user_name)=lower($1) and not e.is_active)
              and exists(select 1 from studio_user_profiles p where lower(p.user_name)=lower($1)
                and p.email_enabled and p.email is not null and lower(p.email)=lower($2))
            """;
        cmd.Parameters.AddWithValue(item.UserName);
        cmd.Parameters.AddWithValue(item.Destination??"");
        return (bool)cmd.ExecuteScalar()!;
    }

    private async Task QueueAsync(PendingNotification item, CancellationToken cancellationToken)
    {
        // Recheck at the actual enqueue boundary; the monitor runs less frequently.
        if(item.Channel=="Email" && !string.IsNullOrWhiteSpace(item.ReferenceType) && item.ReferenceType=="Job" &&
            !IsWorkflowEmailStillAllowed(item))
        {
            Update(item.Id,"Cancelled","Recipient is inactive or email preferences have changed.",false,false);
            return;
        }
        MessageChannel channel = item.Channel switch
        {
            "Email" => MessageChannel.MsEmail,
            "Slack" => MessageChannel.Slack,
            "Teams" => MessageChannel.MsTeams,
            "WhatsApp" => MessageChannel.WhatsApp,
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
            operationsFlows.TryEnqueueNotification(
                CorrelationId(item),
                item.Channel,
                businessId: item.Id.ToString("D"),
                detail: "ProviderUnavailable",
                failed: true);
            return;
        }

        IReadOnlyList<MessageRecipient> resolved = await this.recipientDirectory.ResolveAsync([item.UserName], cancellationToken);
        MessageRecipient? recipient = resolved.FirstOrDefault();
        // Quotes use an explicitly selected persisted client and the destination captured at send time.
        if(item.ReferenceType=="ClientQuote" && item.UserName.StartsWith("client:",StringComparison.Ordinal) && Guid.TryParse(item.UserName[7..],out _) && System.Net.Mail.MailAddress.TryCreate(item.Destination,out _))
            recipient=new MessageRecipient(item.UserName,item.UserName,item.Destination,PreferredChannels:MessageChannel.MsEmail);
        if (recipient is null)
        {
            MarkRetry(item.Id, "Recipient profile could not be resolved.");
            operationsFlows.TryEnqueueNotification(
                CorrelationId(item),
                item.Channel,
                businessId: item.Id.ToString("D"),
                detail: "RecipientUnresolved",
                failed: true);
            return;
        }

        Dictionary<string, string>? metadata = string.IsNullOrWhiteSpace(item.Destination)
            ? null
            : new Dictionary<string, string> { ["Destination"] = item.Destination };
        string correlationId =
            !string.IsNullOrWhiteSpace(item.ReferenceId)
                ? item.ReferenceId.Trim()
                : item.Id.ToString("D");

        using (var epochConnection = database.OpenConnection())
        using (var epoch = new NpgsqlCommand("select value from studio_state where key='studio-dataset-epoch'", epochConnection))
            if (epoch.ExecuteScalar() is string generation)
            { metadata ??= new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); metadata["StudioDatasetEpoch"] = generation; }

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
              and q.state in ('completed','dead')
            returning n.id,n.channel,n.reference_id,n.status
            """;
        command.Parameters.AddWithValue(QueueName);
        using NpgsqlDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            Guid id = reader.GetGuid(0);
            string branch = reader.GetString(1);
            string? referenceId = reader.IsDBNull(2) ? null : reader.GetString(2);
            string status = reader.GetString(3);
            operationsFlows.TryEnqueueNotification(
                string.IsNullOrWhiteSpace(referenceId) ? id.ToString("D") : referenceId,
                branch,
                businessId: id.ToString("D"),
                detail: status == "ProviderAccepted" ? "ProviderAccepted" : "DeadLetter",
                failed: status == "Failed");
        }
    }

    private static string CorrelationId(PendingNotification item) =>
        StudioFlowSemantics.CorrelationId(item.ReferenceId, item.Id);

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
