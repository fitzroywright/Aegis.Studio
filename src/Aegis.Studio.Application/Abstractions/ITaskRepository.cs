using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.Application.Abstractions;

public interface ITaskRepository
{
    Task<StudioTask?> GetAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StudioTask>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task AddAsync(StudioTask task, CancellationToken cancellationToken);
}
