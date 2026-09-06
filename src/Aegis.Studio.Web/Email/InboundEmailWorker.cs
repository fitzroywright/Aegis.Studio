using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Demo;

namespace Aegis.Studio.Web.Email;

public sealed class InboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly IInboundEmailRouter router;
    private readonly DemoStore store;
    private readonly UnroutedEmailStore unroutedStore;
    private readonly ILogger<InboundEmailWorker> logger;

    public InboundEmailWorker(
        MicrosoftGraphEmailClient graphClient,
        IInboundEmailRouter router,
        DemoStore store,
        UnroutedEmailStore unroutedStore,
        ILogger<InboundEmailWorker> logger)
    {
        this.graphClient = graphClient;
        this.router = router;
        this.store = store;
        this.unroutedStore = unroutedStore;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!graphClient.IsConfigured)
        {
            logger.LogInformation("Microsoft Graph email ingestion is disabled or not configured.");
            return;
        }

        logger.LogInformation("Microsoft Graph email ingestion started for the AegisHub collection mailbox.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<InboundEmail> messages = await graphClient.GetInboxMessagesAsync(stoppingToken);
                foreach (InboundEmail email in messages)
                {
                    EmailRouteResult route = router.Route(email);
                    if (!route.IsRouted)
                    {
                        UnroutedEmail captured = unroutedStore.Capture(
                            email,
                            route.FailureReason ?? "No route was resolved.");

                        logger.LogWarning(
                            "Inbound email {MessageId} from {Sender} needs routing review. Review item {ReviewId}. To: {To}; Cc: {Cc}",
                            email.MessageId,
                            email.FromAddress,
                            captured.Id,
                            string.Join(", ", email.ToAddresses),
                            string.Join(", ", email.CcAddresses));
                        continue;
                    }

                    EmailIngestResult result = store.ReceiveEmail(email, route);
                    if (result.WasDuplicate)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "Inbound email {MessageId} routed to {Workspace} conversation {ConversationId}. New conversation: {CreatedConversation}",
                        email.MessageId,
                        route.WorkspaceName,
                        result.Conversation.Id,
                        result.CreatedConversation);
                }
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
