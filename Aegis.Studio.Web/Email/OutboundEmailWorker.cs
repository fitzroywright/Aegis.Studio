using Aegis.Studio.Infrastructure.Email;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly OutboundEmailStore outbox;
    private readonly StudioOperationsFlowPublisher operationsFlows;
    private readonly ILogger<OutboundEmailWorker> logger;

    public OutboundEmailWorker(
        MicrosoftGraphEmailClient graphClient,
        OutboundEmailStore outbox,
        StudioOperationsFlowPublisher operationsFlows,
        ILogger<OutboundEmailWorker> logger)
    {
        this.graphClient = graphClient;
        this.outbox = outbox;
        this.operationsFlows = operationsFlows;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool configurationWarningLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!graphClient.IsConfigured)
            {
                if (!configurationWarningLogged)
                {
                    logger.LogWarning("Microsoft Graph outbound email is waiting for configuration. Pending messages will remain queued.");
                    configurationWarningLogged = true;
                }

                try
                {
                    await Task.Delay(graphClient.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            configurationWarningLogged = false;
            try
            {
                IReadOnlyList<OutboundEmail> pending = outbox.GetPending();
                foreach (OutboundEmail message in pending)
                {
                    try
                    {
                        await graphClient.SendEmailAsync(
                            message.FromAddress,
                            message.ToAddress,
                            message.Subject,
                            message.Body,
                            stoppingToken);

                        outbox.MarkProviderAccepted(message.Id, null);
                        operationsFlows.TryEnqueueOutbound(
                            message.ConversationId.ToString("D"),
                            "Provider",
                            businessId: message.Id.ToString("D"),
                            detail: "ProviderAccepted");
                        operationsFlows.TryEnqueueOutboundUnknown(
                            message.ConversationId.ToString("D"),
                            "Recipient",
                            businessId: message.Id.ToString("D"),
                            detail: "DeliveryReceiptUnavailable");
                        logger.LogInformation(
                            "Sent outbound email {MessageId} from {FromAddress} to {ToAddress}.",
                            message.Id,
                            message.FromAddress,
                            message.ToAddress);
                    }
                    catch (Exception exception)
                    {
                        double retrySeconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(message.Attempts, 6)));
                        OutboundEmail? updated = outbox.MarkFailure(
                            message.Id,
                            exception.GetType().Name,
                            DateTimeOffset.UtcNow.AddSeconds(retrySeconds));

                        operationsFlows.TryEnqueueOutbound(
                            message.ConversationId.ToString("D"),
                            "Provider",
                            businessId: message.Id.ToString("D"),
                            detail: updated?.Status == "Failed" ? "RetryExhausted" : exception.GetType().Name,
                            failed: true);

                        logger.LogError(
                            exception,
                            "Failed sending outbound email {MessageId} from {FromAddress} to {ToAddress}. Attempt={Attempt}; State={State}.",
                            message.Id,
                            message.FromAddress,
                            message.ToAddress,
                            (updated?.Attempts ?? message.Attempts + 1),
                            updated?.Status ?? "Unknown");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbound email processing cycle failed.");
            }

            try
            {
                await Task.Delay(graphClient.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
