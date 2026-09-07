using Aegis.Studio.Infrastructure.Email;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly OutboundEmailStore outbox;
    private readonly ILogger<OutboundEmailWorker> logger;

    public OutboundEmailWorker(
        MicrosoftGraphEmailClient graphClient,
        OutboundEmailStore outbox,
        ILogger<OutboundEmailWorker> logger)
    {
        this.graphClient = graphClient;
        this.outbox = outbox;
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

                        outbox.MarkSent(message.Id, null);
                        logger.LogInformation(
                            "Sent outbound email {MessageId} from {FromAddress} to {ToAddress}.",
                            message.Id,
                            message.FromAddress,
                            message.ToAddress);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Failed sending outbound email {MessageId} from {FromAddress} to {ToAddress}.",
                            message.Id,
                            message.FromAddress,
                            message.ToAddress);
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
