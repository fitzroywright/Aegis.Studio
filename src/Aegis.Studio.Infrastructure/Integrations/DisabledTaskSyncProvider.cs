using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.Infrastructure.Integrations;

public sealed class DisabledTaskSyncProvider : ITaskSyncProvider
{
    public Task<IntegrationResult> UpsertAsync(StudioTask task, CancellationToken cancellationToken) => Task.FromResult(new IntegrationResult(false, false, null, "External task synchronization is not configured."));
}
