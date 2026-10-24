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
            bool processingMessage = false;
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
                    processingMessage = true;
                    await ProcessMessageAsync(email, (message, token) => intake.ReceiveAsync(message, token),
                        operationsFlows, logger, stoppingToken);
                    processingMessage = false;
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
                // Message failures are already logged with their identifiers. Do not duplicate them.
                if (!processingMessage)
                    logger.LogError("Microsoft Graph email ingestion cycle failed. ErrorType={ErrorType}", exception.GetType().Name);
            }

            await Task.Delay(graphClient.PollInterval, stoppingToken);
        }
    }

    // Keep the receipt and routing boundary together so failures cannot hide a retrieved email.
    internal static async Task ProcessMessageAsync(InboundEmail email,
        Func<InboundEmail, CancellationToken, Task<StudioEmailIntakeResult>> receive,
        StudioOperationsFlowPublisher flows, ILogger logger, CancellationToken token)
    {
        string correlationId = email.MessageId;
        logger.LogInformation(new EventId(22101, "InboundEmailReceived"),
            "Inbound email received from Microsoft Graph. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} Sender={Sender} Subject={Subject} ReceivedAtUtc={ReceivedAtUtc} AttachmentCount={AttachmentCount}",
            email.MessageId, email.ProviderMessageId, correlationId, email.FromAddress, email.Subject,
            email.ReceivedAt.ToUniversalTime(), email.FileAttachments.Count);
        try
        {
            flows.TryEnqueueInbound(correlationId, "Mailbox");
            var result = await receive(email, token);
            flows.TryEnqueueInbound(correlationId, "Router",
                businessId: (result.ConversationId ?? result.ReviewId)?.ToString("D"),
                detail: result.Routed ? "Associated" : "Review", failed: !result.Routed);
            logger.LogInformation(new EventId(22102, "InboundEmailRoutingCompleted"),
                "Inbound email routing completed. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} Routed={Routed} ConversationId={ConversationId} ReviewId={ReviewId} WorkspaceId={WorkspaceId} WasDuplicate={WasDuplicate}",
                email.MessageId, email.ProviderMessageId, correlationId, result.Routed,
                result.ConversationId, result.ReviewId, result.WorkspaceId, result.WasDuplicate);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            flows.TryEnqueueInbound(correlationId, "Router", detail: exception.GetType().Name, failed: true);
            // Provider/storage exception text may contain email content. Log only the error type.
            logger.LogError(new EventId(22103, "InboundEmailRoutingFailed"),
                "Inbound email routing failed. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} ErrorType={ErrorType}",
                email.MessageId, email.ProviderMessageId, correlationId, exception.GetType().Name);
            throw; // Abort the batch and retain its delta checkpoint for retry.
        }
    }
}

