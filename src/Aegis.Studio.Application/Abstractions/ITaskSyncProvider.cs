using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.Application.Abstractions;

public interface ITaskSyncProvider
{
    Task<IntegrationResult> UpsertAsync(StudioTask task, CancellationToken cancellationToken);
}
