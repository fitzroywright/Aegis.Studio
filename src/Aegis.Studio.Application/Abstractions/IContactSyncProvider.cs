using Aegis.Studio.Domain.Customers;

namespace Aegis.Studio.Application.Abstractions;

public interface IContactSyncProvider
{
    Task<IntegrationResult> UpsertAsync(Customer customer, CancellationToken cancellationToken);
}
