using Common.Messaging;
using Aegis.Studio.Web.Messaging;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailWorker : BackgroundService
{
    private readonly IExternalMessageChannel? emailChannel;
    private readonly OutboundEmailStore outbox;
    private readonly StudioOperationsFlowPublisher operationsFlows;
    private readonly StudioMessageAttachmentStore attachments;
    private readonly ILogger<OutboundEmailWorker> logger;
    private readonly TimeSpan pollInterval;

    public OutboundEmailWorker(
        IEnumerable<IExternalMessageChannel> externalChannels,
        IConfiguration configuration,
        OutboundEmailStore outbox,
        StudioOperationsFlowPublisher operationsFlows,
        StudioMessageAttachmentStore attachments,
        ILogger<OutboundEmailWorker> logger)
    {
        emailChannel = externalChannels.FirstOrDefault(channel => channel.Channel == MessageChannel.MsEmail);
        this.outbox = outbox;
        this.operationsFlows = operationsFlows;
        this.attachments = attachments;
        this.logger = logger;
        pollInterval = TimeSpan.FromSeconds(Math.Max(
            5,
            configuration.GetValue<int?>("Messaging:DeliveryPollSeconds") ?? 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool configurationWarningLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (emailChannel is null)
            {
                if (!configurationWarningLogged)
                {
                    logger.LogWarning("Common.Messaging Microsoft 365 email is waiting for configuration. Pending messages will remain queued.");
                    configurationWarningLogged = true;
                }

                try
                {
                    await Task.Delay(pollInterval, stoppingToken);
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
                        MessageRecipient recipient = new(
                            message.ToAddress,
                            message.ToAddress,
                            message.ToAddress,
                            PreferredChannels: MessageChannel.MsEmail);
                        IReadOnlyCollection<Common.Messaging.MessageAttachment> deliveryAttachments =
                            message.InAppMessageId is Guid inAppMessageId
                                ? attachments.GetForMessage(inAppMessageId)
                                    .Select(file => new Common.Messaging.MessageAttachment(
                                        file.FileName,
                                        file.ContentType,
                                        attachments.ReadBytes(file)))
                                    .ToArray()
                                : Array.Empty<Common.Messaging.MessageAttachment>();
                        MessageRequest request = new()
                        {
                            RecipientIds = [message.ToAddress],
                            Title = message.Subject,
                            Body = message.Body,
                            BodyIsHtml = false,
                            Attachments = deliveryAttachments,
                            Channels = MessageChannel.MsEmail,
                            Source = "Aegis.Studio",
                            CorrelationId = message.ConversationId.ToString("D"),
                            Metadata = new Dictionary<string,string>
                            {
                                ["StudioLogicalFrom"] = message.FromAddress,
                                ["StudioConversationId"] = message.ConversationId.ToString("D"),
                                ["ReplyTo"] = message.SendingIdentityType == nameof(StudioSendingIdentityType.Person)
                                    ? message.FromAddress
                                    : string.Empty
                            }
                        };

                        await emailChannel.SendAsync(
                            recipient,
                            request,
                            message.Id,
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
                            "Common.Messaging accepted outbound email {MessageId} to {ToAddress}.",
                            message.Id,
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
                            "Failed sending outbound email {MessageId} through Common.Messaging. Attempt={Attempt}; State={State}.",
                            message.Id,
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
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
