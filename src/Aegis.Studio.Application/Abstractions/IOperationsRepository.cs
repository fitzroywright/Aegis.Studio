using Aegis.Studio.Domain.Operations;

namespace Aegis.Studio.Application.Abstractions;

public interface IOperationsRepository
{
    Task AddAlertAsync(OperationalAlert alert, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperationalAlert>> GetOpenAlertsAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task<OperationalAlert?> GetAlertAsync(Guid id, CancellationToken cancellationToken);
}
