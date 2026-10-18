using Aegis.Studio.Application.EmailRouting;
using Common.Messaging.Channels.MicrosoftGraph;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Email;

public sealed class InboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphMailboxClient graphClient;
    private readonly StudioEmailIntake intake;
    private readonly StudioDatabase database;
    private readonly GraphEmailDeltaStateStore deltaStateStore;
    private readonly StudioOperationsFlowPublisher operationsFlows;
    private readonly ILogger<InboundEmailWorker> logger;

    public InboundEmailWorker(MicrosoftGraphMailboxClient graphClient,StudioEmailIntake intake,StudioDatabase database,
        GraphEmailDeltaStateStore deltaStateStore,StudioOperationsFlowPublisher operationsFlows,ILogger<InboundEmailWorker> logger)
    {
        this.graphClient=graphClient;this.intake=intake;this.database=database;
        this.deltaStateStore=deltaStateStore;this.operationsFlows=operationsFlows;this.logger=logger;
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
                using var operation = Aegis.Studio.Web.Operations.DemoMaintenance.TryEnterOperation(database);
                if (operation is null) { await Task.Delay(1000, stoppingToken); continue; }
                string? deltaLink = deltaStateStore.GetDeltaLink();
                MicrosoftGraphInboxDeltaBatch batch = await graphClient.GetInboxChangesAsync(deltaLink, stoppingToken);

                foreach (MicrosoftGraphInboundMessage graphEmail in batch.Messages)
                {
                    InboundEmail email = new(
                        graphEmail.MessageId,
                        graphEmail.ConversationId,
                        graphEmail.FromName,
                        graphEmail.FromAddress,
                        graphEmail.ToAddresses,
                        graphEmail.CcAddresses,
                        graphEmail.Headers,
                        graphEmail.Subject,
                        graphEmail.Body,
                        graphEmail.ReceivedAtUtc,
                        graphEmail.FileAttachments.Select(a => new InboundEmailAttachment(a.FileName, a.ContentType, a.Content, a.IsInline)).ToList(), graphEmail.ProviderMessageId);
                    string correlationId = email.MessageId;
                    operationsFlows.TryEnqueueInbound(correlationId, "Mailbox");
                    var result=await intake.ReceiveAsync(email,stoppingToken);
                    operationsFlows.TryEnqueueInbound(correlationId,"Router",businessId:(result.ConversationId??result.ReviewId)?.ToString("D"),
                        detail:result.Routed?"Associated":"Review",failed:!result.Routed);
                    logger.LogInformation("Inbound email {MessageId}: routed {Routed}, conversation {ConversationId}, review {ReviewId}",
                        email.MessageId,result.Routed,result.ConversationId,result.ReviewId);
                }

                if (!string.IsNullOrWhiteSpace(batch.DeltaLink))
                {
                    deltaStateStore.SaveDeltaLink(batch.DeltaLink);
                }
            }
            catch (MicrosoftGraphDeltaTokenExpiredException)
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

