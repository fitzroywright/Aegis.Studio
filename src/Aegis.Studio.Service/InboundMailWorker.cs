using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Communications;

namespace Aegis.Studio.Service;

public sealed class InboundMailWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<InboundMailWorker> logger;

    public InboundMailWorker(IServiceScopeFactory scopeFactory, ILogger<InboundMailWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Inbound mail processing failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IInboundMailProvider provider = scope.ServiceProvider.GetRequiredService<IInboundMailProvider>();
        InboundMailRouter router = scope.ServiceProvider.GetRequiredService<InboundMailRouter>();
        IReadOnlyList<InboundMailItem> messages = await provider.GetUnreadAsync(cancellationToken);

        foreach (InboundMailItem message in messages)
        {
            try
            {
                InboundMailRouteResult result = await router.RouteAsync(message, cancellationToken);
                await provider.MarkProcessedAsync(message.ProviderMessageId, cancellationToken);

                logger.LogInformation
                (
                    "Inbound email {ProviderMessageId} from {ClientEmail} to {RecipientAddress} routed to workspace {WorkspaceName} ({WorkspaceId}), conversation {ConversationId}, booking {BookingId}.",
                    message.ProviderMessageId,
                    result.ClientEmail,
                    result.RecipientAddress,
                    result.WorkspaceName,
                    result.WorkspaceId,
                    result.ConversationId,
                    result.BookingId
                );
            }
            catch (Exception exception)
            {
                logger.LogError
                (
                    exception,
                    "Inbound email {ProviderMessageId} from {FromAddress} to {RecipientAddress} could not be routed. The message remains unprocessed for retry.",
                    message.ProviderMessageId,
                    message.FromAddress,
                    message.OriginalRecipientAddress
                );
            }
        }
    }
}
