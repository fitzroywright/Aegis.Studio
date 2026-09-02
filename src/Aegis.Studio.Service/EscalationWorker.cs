using Aegis.Studio.Application.Operations;

namespace Aegis.Studio.Service;

public sealed class EscalationWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<EscalationWorker> logger;

    public EscalationWorker(IServiceScopeFactory scopeFactory, ILogger<EscalationWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            EscalationService service = scope.ServiceProvider.GetRequiredService<EscalationService>();
            int created = await service.EvaluateAsync(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), stoppingToken);
            if (created > 0)
            {
                logger.LogWarning("Created {Count} Workspace escalation alerts.", created);
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }
}
