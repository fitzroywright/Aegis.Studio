using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class CustomerRepository : ICustomerRepository
{
    private readonly StudioDbContext dbContext;

    public CustomerRepository(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<Customer?> GetByEmailAsync(string email, CancellationToken cancellationToken) => await dbContext.Customers.SingleOrDefaultAsync(item => item.Email == email.Trim().ToLowerInvariant(), cancellationToken);

    public async Task<IReadOnlyList<Customer>> SearchAsync(string? search, CancellationToken cancellationToken)
    {
        IQueryable<Customer> query = dbContext.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where(item => item.Name.ToLower().Contains(term) || item.Email.ToLower().Contains(term) || item.Phone.Contains(term));
        }
        return await query.OrderBy(item => item.Name).Take(200).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken) => await dbContext.Customers.AddAsync(customer, cancellationToken);
}
