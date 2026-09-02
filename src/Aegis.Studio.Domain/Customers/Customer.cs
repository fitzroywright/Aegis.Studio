namespace Aegis.Studio.Domain.Customers;

public sealed class Customer
{
    private Customer()
    {
    }

    public Customer(string name, string email, string phone)
    {
        Id = Guid.NewGuid();
        Name = name.Trim();
        Email = email.Trim().ToLowerInvariant();
        Phone = phone.Trim();
        CreatedUtc = DateTimeOffset.UtcNow;
        UpdatedUtc = CreatedUtc;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    public void Update(string name, string phone)
    {
        Name = name.Trim();
        Phone = phone.Trim();
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
