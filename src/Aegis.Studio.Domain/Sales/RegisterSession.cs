namespace Aegis.Studio.Domain.Sales;

public sealed class RegisterSession
{
    private RegisterSession()
    {
    }

    public RegisterSession(string registerName, string cashierUserName, decimal openingFloat)
    {
        if (openingFloat < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingFloat));
        }

        Id = Guid.NewGuid();
        RegisterName = registerName.Trim();
        CashierUserName = cashierUserName.Trim();
        OpeningFloat = openingFloat;
        OpenedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string RegisterName { get; private set; } = string.Empty;
    public string CashierUserName { get; private set; } = string.Empty;
    public decimal OpeningFloat { get; private set; }
    public decimal? CountedCash { get; private set; }
    public DateTimeOffset OpenedUtc { get; private set; }
    public DateTimeOffset? ClosedUtc { get; private set; }
    public bool IsClosed => ClosedUtc.HasValue;

    public void Close(decimal countedCash)
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("Register session is already closed.");
        }

        if (countedCash < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(countedCash));
        }

        CountedCash = countedCash;
        ClosedUtc = DateTimeOffset.UtcNow;
    }
}
