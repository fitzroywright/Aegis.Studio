using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Customers;

namespace Aegis.Studio.Infrastructure.Integrations;

public sealed class DisabledContactSyncProvider : IContactSyncProvider
{
    public Task<IntegrationResult> UpsertAsync(Customer customer, CancellationToken cancellationToken) => Task.FromResult(new IntegrationResult(false, false, null, "Contact synchronization is not configured."));
}
