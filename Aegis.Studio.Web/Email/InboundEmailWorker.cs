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
                    await RouteReceivedAsync(email, (received, token) => intake.ReceiveAsync(received, token),
                        operationsFlows, logger, stoppingToken);
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
                logger.LogError("Microsoft Graph email ingestion cycle failed. FailureType={FailureType}", exception.GetType().Name);
            }

            await Task.Delay(graphClient.PollInterval, stoppingToken);
        }
    }

    internal static async Task<StudioEmailIntakeResult> RouteReceivedAsync(
        InboundEmail email,
        Func<InboundEmail, CancellationToken, Task<StudioEmailIntakeResult>> receive,
        StudioOperationsFlowPublisher flows,
        ILogger<InboundEmailWorker> log,
        CancellationToken token)
    {
        // Explicit fields render in the default systemd console log, without requiring scopes.
        // Never serialize the inbound envelope: it contains bodies, headers and attachment bytes.
        log.LogInformation("Inbound email received from Microsoft Graph. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} Sender={Sender} Subject={Subject} ReceivedAtUtc={ReceivedAtUtc:O} AttachmentCount={AttachmentCount}",
            email.MessageId, email.ProviderMessageId, email.MessageId, email.FromAddress,
            email.Subject, email.ReceivedAt.ToUniversalTime(), email.FileAttachments.Count);
        flows.TryEnqueueInbound(email.MessageId, "Mailbox");
        StudioEmailIntakeResult result;
        try
        {
            result = await receive(email, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            flows.TryEnqueueInbound(email.MessageId, "Router", detail:exception.GetType().Name, failed:true);
            log.LogError("Inbound email routing failed. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} FailureType={FailureType}",
                email.MessageId, email.ProviderMessageId, email.MessageId, exception.GetType().Name);
            throw;
        }
        flows.TryEnqueueInbound(email.MessageId, "Router", businessId:(result.ConversationId ?? result.ReviewId)?.ToString("D"),
            detail:result.Routed ? "Associated" : "Review", failed:!result.Routed);
        log.LogInformation("Inbound email routing completed. MessageId={MessageId} ProviderMessageId={ProviderMessageId} CorrelationId={CorrelationId} Routed={Routed} ConversationId={ConversationId} ReviewId={ReviewId} WasDuplicate={WasDuplicate}",
            email.MessageId, email.ProviderMessageId, email.MessageId, result.Routed,
            result.ConversationId, result.ReviewId, result.WasDuplicate);
        return result;
    }
}


