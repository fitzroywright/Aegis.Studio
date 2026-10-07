using System.Security.Cryptography;
using Aegis.Studio.Application.EmailRouting;
using Common.Messaging.Channels.MicrosoftGraph;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.Messaging;

namespace Aegis.Studio.Web.Email;

public sealed class InboundEmailWorker : BackgroundService
{
    private readonly MicrosoftGraphMailboxClient graphClient;
    private readonly IInboundEmailRouter router;
    private readonly StudioStore store;
    private readonly StudioDatabase database;
    private readonly UnroutedEmailStore unroutedStore;
    private readonly OutboundEmailStore outboundStore;
    private readonly GraphEmailDeltaStateStore deltaStateStore;
    private readonly NotificationService notifications;
    private readonly AttachmentStore attachments;
    private readonly StudioOperationsFlowPublisher operationsFlows;
    private readonly StudioInAppMessageStore messages;
    private readonly StudioMessageAttachmentStore messageAttachments;
    private readonly ILogger<InboundEmailWorker> logger;

    public InboundEmailWorker(
        MicrosoftGraphMailboxClient graphClient,
        IInboundEmailRouter router,
        StudioStore store,
        StudioDatabase database,
        UnroutedEmailStore unroutedStore,
        OutboundEmailStore outboundStore,
        GraphEmailDeltaStateStore deltaStateStore,
        NotificationService notifications,
        AttachmentStore attachments,
        StudioOperationsFlowPublisher operationsFlows,
        StudioInAppMessageStore messages,
        StudioMessageAttachmentStore messageAttachments,
        ILogger<InboundEmailWorker> logger)
    {
        this.graphClient = graphClient;
        this.router = router;
        this.store = store;
        this.database = database;
        this.unroutedStore = unroutedStore;
        this.outboundStore = outboundStore;
        this.deltaStateStore = deltaStateStore;
        this.notifications = notifications;
        this.attachments = attachments;
        this.operationsFlows = operationsFlows;
        this.messages = messages;
        this.messageAttachments = messageAttachments;
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
                        graphEmail.FileAttachments.Select(a => new InboundEmailAttachment(a.FileName, a.ContentType, a.Content, a.IsInline)).ToList());
                    string correlationId = email.MessageId;
                    operationsFlows.TryEnqueueInbound(correlationId, "Mailbox");
                    EmailRouteResult route = router.Route(email);
                    if (!route.IsRouted)
                    {
                        UnroutedEmail captured = unroutedStore.Capture(email, route.FailureReason ?? "No route was resolved.");
                        operationsFlows.TryEnqueueInbound(correlationId, "Router", businessId: captured.Id.ToString("D"), detail: "RouteUnresolved", failed: true);
                        logger.LogWarning("Inbound email {MessageId} from {Sender} needs routing review. Review item {ReviewId}. To: {To}; Cc: {Cc}", email.MessageId, email.FromAddress, captured.Id, string.Join(", ", email.ToAddresses), string.Join(", ", email.CcAddresses));
                        continue;
                    }

                    AegisReferenceRecoveryResult recovery = AegisReferenceRecovery.TryRecover(database, store, email, route);
                    if (recovery.Ambiguous)
                    {
                        string reason = $"AEGIS reference {recovery.ReferenceToken} matched more than one active conversation for this sender in the routed workspace.";
                        UnroutedEmail captured = unroutedStore.Capture(email, reason);
                        operationsFlows.TryEnqueueInbound(correlationId, "Router", businessId: captured.Id.ToString("D"), detail: "AmbiguousReference", failed: true);
                        logger.LogWarning("Inbound email {MessageId} contains ambiguous AEGIS reference {Reference}. Review item {ReviewId}.", email.MessageId, recovery.ReferenceToken, captured.Id);
                        continue;
                    }

                    operationsFlows.TryEnqueueInbound(correlationId, "Router", businessId: route.WorkspaceId?.ToString("D"));
                    EmailIngestResult result = recovery.Matched
                        ? recovery.IngestResult!
                        : store.ReceiveEmail(email, route);
                    operationsFlows.TryEnqueueInbound(correlationId, "Workspace", businessId: result.Conversation.Id.ToString("D"));
                    operationsFlows.TryEnqueueInbound(correlationId, "Conversation", businessId: result.Conversation.Id.ToString("D"));

                    StudioMessageThread? personThread = null;
                    StudioInAppMessage? personMessage = null;
                    if (route.DestinationType.Equals("Person", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(route.PersonUserName))
                    {
                        personThread = messages.EnsurePersonThread(result.Conversation, route.PersonUserName!);
                        personMessage = messages.AddExternalMessage(personThread.Id, email);
                    }

                    int attachmentIndex = 0;
                    foreach (InboundEmailAttachment attachment in email.FileAttachments.Where(x => !x.IsInline))
                    {
                        attachmentIndex++;
                        string contentHash = Convert.ToHexString(SHA256.HashData(attachment.Content)).ToLowerInvariant();
                        string sourceKey = $"graph:{email.MessageId}:{attachment.FileName}:{contentHash}";
                        string attachmentCorrelation = $"{email.MessageId}:attachment:{attachmentIndex}";

                        operationsFlows.TryEnqueueAttachment(
                            attachmentCorrelation,
                            "Received",
                            businessId: result.Conversation.Id.ToString("D"));

                        try
                        {
                            StudioAttachment storedAttachment = await attachments.AddBytesAsync(
                                result.Conversation.Id,
                                attachment.FileName,
                                attachment.ContentType,
                                attachment.Content,
                                "External",
                                $"Microsoft Graph: {email.FromAddress}",
                                stoppingToken,
                                sourceKey,
                                attachmentCorrelation);

                            operationsFlows.TryEnqueueAttachment(
                                attachmentCorrelation,
                                "Storage",
                                businessId: storedAttachment.Id.ToString("D"));
                            operationsFlows.TryEnqueueAttachment(
                                attachmentCorrelation,
                                "Metadata",
                                businessId: storedAttachment.Id.ToString("D"));
                            operationsFlows.TryEnqueueAttachment(
                                attachmentCorrelation,
                                "Workspace",
                                businessId: storedAttachment.Id.ToString("D"));
                            if (personMessage is not null)
                            {
                                await messageAttachments.AddBytesAsync(
                                    personMessage.Id,
                                    attachment.FileName,
                                    attachment.ContentType,
                                    attachment.Content,
                                    $"External: {email.FromAddress}",
                                    stoppingToken);
                            }
                        }
                        catch (Exception exception)
                        {
                            operationsFlows.TryEnqueueAttachment(
                                attachmentCorrelation,
                                "Storage",
                                businessId: result.Conversation.Id.ToString("D"),
                                detail: exception.GetType().Name,
                                failed: true);
                            throw;
                        }
                    }

                    if (!result.WasDuplicate)
                    {
                        if (result.CreatedConversation &&
                            route.DestinationType.Equals("Workspace", StringComparison.OrdinalIgnoreCase))
                        {
                            outboundStore.QueueAcknowledgement(result.Conversation, route.WorkspaceName ?? "Studio");
                        }

                        StudioMessageNotificationContext.PublishInbound(
                            notifications,
                            email,
                            route,
                            result.Conversation.Id,
                            personThread?.Id);
                        operationsFlows.TryEnqueueInbound(correlationId, "Notification", businessId: result.Conversation.Id.ToString("D"));
                    }

                    logger.LogInformation(
                        "Inbound email {MessageId} routed to {DestinationType} {Destination} conversation {ConversationId}. New conversation: {CreatedConversation}. Reference recovery: {ReferenceRecovered}. Attachments: {AttachmentCount}",
                        email.MessageId,
                        route.DestinationType,
                        route.UserFacingContext,
                        result.Conversation.Id,
                        result.CreatedConversation,
                        recovery.Matched,
                        email.FileAttachments.Count);
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
