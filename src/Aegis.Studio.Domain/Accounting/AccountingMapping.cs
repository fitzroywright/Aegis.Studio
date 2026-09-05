namespace Aegis.Studio.Domain.Accounting;

public sealed class AccountingMapping
{
    private AccountingMapping()
    {
    }

    public AccountingMapping(string studioCode, string accountCode, string description)
    {
        Id = Guid.NewGuid();
        StudioCode = studioCode.Trim();
        AccountCode = accountCode.Trim();
        Description = description.Trim();
    }

    public Guid Id { get; private set; }
    public string StudioCode { get; private set; } = string.Empty;
    public string AccountCode { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    public void ChangeAccount(string accountCode)
    {
        AccountCode = accountCode.Trim();
    }
}
