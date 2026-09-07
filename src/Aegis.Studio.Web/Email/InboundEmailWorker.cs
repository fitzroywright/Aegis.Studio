using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;

namespace Aegis.Studio.Web.Email;

public sealed class InboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly IInboundEmailRouter router;
    private readonly StudioStore store;
    private readonly UnroutedEmailStore unroutedStore;
    private readonly OutboundEmailStore outboundStore;
    private readonly GraphEmailDeltaStateStore deltaStateStore;
    private readonly NotificationService notifications;
    private readonly ILogger<InboundEmailWorker> logger;

    public InboundEmailWorker(
        MicrosoftGraphEmailClient graphClient,
        IInboundEmailRouter router,
        StudioStore store,
        UnroutedEmailStore unroutedStore,
        OutboundEmailStore outboundStore,
        GraphEmailDeltaStateStore deltaStateStore,
        NotificationService notifications,
        ILogger<InboundEmailWorker> logger)
    {
        this.graphClient = graphClient;
        this.router = router;
        this.store = store;
        this.unroutedStore = unroutedStore;
        this.outboundStore = outboundStore;
        this.deltaStateStore = deltaStateStore;
        this.notifications = notifications;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!graphClient.IsConfigured)
        {
            logger.LogWarning("Microsoft Graph email ingestion is enabled but credentials are incomplete. Configure the client secret and restart Studio.");
            return;
        }

        logger.LogInformation("Microsoft Graph email ingestion started for the collection mailbox.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? deltaLink = deltaStateStore.GetDeltaLink();
                GraphInboxDeltaBatch batch = await graphClient.GetInboxChangesAsync(deltaLink, stoppingToken);

                foreach (InboundEmail email in batch.Messages)
                {
                    EmailRouteResult route = router.Route(email);
                    if (!route.IsRouted)
                    {
                        UnroutedEmail captured = unroutedStore.Capture(email, route.FailureReason ?? "No route was resolved.");
                        logger.LogWarning("Inbound email {MessageId} from {Sender} needs routing review. Review item {ReviewId}. To: {To}; Cc: {Cc}", email.MessageId, email.FromAddress, captured.Id, string.Join(", ", email.ToAddresses), string.Join(", ", email.CcAddresses));
                        continue;
                    }

                    EmailIngestResult result = store.ReceiveEmail(email, route);
                    if (result.WasDuplicate)
                    {
                        continue;
                    }

                    if (result.CreatedConversation)
                    {
                        outboundStore.QueueAcknowledgement(result.Conversation, route.WorkspaceName ?? "Studio");
                    }

                    notifications.PublishToAll(
                        "WorkspaceMessage",
                        $"Email: {email.Subject}",
                        $"New email from {email.FromName} routed to {route.WorkspaceName}.",
                        "Conversation",
                        result.Conversation.Id.ToString("D"));

                    logger.LogInformation("Inbound email {MessageId} routed to {Workspace} conversation {ConversationId}. New conversation: {CreatedConversation}", email.MessageId, route.WorkspaceName, result.Conversation.Id, result.CreatedConversation);
                }

                if (!string.IsNullOrWhiteSpace(batch.DeltaLink))
                {
                    deltaStateStore.SaveDeltaLink(batch.DeltaLink);
                }
            }
            catch (GraphDeltaTokenExpiredException)
            {
                logger.LogWarning("Microsoft Graph delta checkpoint expired. Clearing it so the next cycle can establish a fresh checkpoint.");
                deltaStateStore.Clear();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Microsoft Graph email ingestion cycle failed.");
            }

            await Task.Delay(graphClient.PollInterval, stoppingToken);
        }
    }
}
