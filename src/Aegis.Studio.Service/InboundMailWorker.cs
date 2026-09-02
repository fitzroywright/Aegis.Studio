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
        CommunicationService service = scope.ServiceProvider.GetRequiredService<CommunicationService>();
        IReadOnlyList<InboundMailItem> messages = await provider.GetUnreadAsync(cancellationToken);
        foreach (InboundMailItem message in messages)
        {
            await service.RouteInboundAsync(message, cancellationToken);
            await provider.MarkProcessedAsync(message.ProviderMessageId, cancellationToken);
        }
    }
}
