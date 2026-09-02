using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Customers;

namespace Aegis.Studio.Application.Customers;

public sealed class CustomerService
{
    private readonly ICustomerRepository customerRepository;
    private readonly IContactSyncProvider contactSyncProvider;
    private readonly IUnitOfWork unitOfWork;

    public CustomerService(ICustomerRepository customerRepository, IContactSyncProvider contactSyncProvider, IUnitOfWork unitOfWork)
    {
        this.customerRepository = customerRepository;
        this.contactSyncProvider = contactSyncProvider;
        this.unitOfWork = unitOfWork;
    }

    public async Task<Customer> UpsertAsync(string name, string email, string phone, CancellationToken cancellationToken)
    {
        Customer? customer = await customerRepository.GetByEmailAsync(email, cancellationToken);
        if (customer is null)
        {
            customer = new Customer(name, email, phone);
            await customerRepository.AddAsync(customer, cancellationToken);
        }
        else
        {
            customer.Update(name, phone);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await contactSyncProvider.UpsertAsync(customer, cancellationToken);
        return customer;
    }
}
