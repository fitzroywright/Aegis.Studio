using Aegis.Studio.Domain.Customers;

namespace Aegis.Studio.Application.Abstractions;

public interface ICustomerRepository
{
    Task<Customer?> GetByEmailAsync(string email, CancellationToken cancellationToken);
    Task<IReadOnlyList<Customer>> SearchAsync(string? search, CancellationToken cancellationToken);
    Task AddAsync(Customer customer, CancellationToken cancellationToken);
}
