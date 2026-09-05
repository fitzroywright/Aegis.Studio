namespace Aegis.Studio.Domain.Accounting;

public sealed class AccountingBatch
{
    private readonly List<AccountingEntry> entries = new();

    private AccountingBatch()
    {
    }

    public AccountingBatch(DateOnly businessDate)
    {
        Id = Guid.NewGuid();
        BusinessDate = businessDate;
        BatchNumber = $"STUDIO-{businessDate:yyyyMMdd}-{Id.ToString("N")[..8].ToUpperInvariant()}";
        Status = AccountingBatchStatus.Pending;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string BatchNumber { get; private set; } = string.Empty;
    public DateOnly BusinessDate { get; private set; }
    public AccountingBatchStatus Status { get; private set; }
    public string ExternalBatchReference { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? PostedUtc { get; private set; }
    public IReadOnlyCollection<AccountingEntry> Entries => entries.AsReadOnly();

    public void AddEntry(string accountCode, decimal debit, decimal credit, string description, string sourceReference)
    {
        if ((debit <= 0 && credit <= 0) || (debit > 0 && credit > 0))
        {
            throw new InvalidOperationException("An accounting entry must contain either a debit or a credit.");
        }

        entries.Add(new AccountingEntry(Id, accountCode, debit, credit, description, sourceReference));
    }

    public void MarkPosted(string externalBatchReference)
    {
        if (Status == AccountingBatchStatus.Posted)
        {
            throw new InvalidOperationException("Accounting batch has already been posted.");
        }

        decimal debits = entries.Sum(entry => entry.Debit);
        decimal credits = entries.Sum(entry => entry.Credit);
        if (debits != credits)
        {
            throw new InvalidOperationException("Accounting batch is not balanced.");
        }

        ExternalBatchReference = externalBatchReference.Trim();
        Status = AccountingBatchStatus.Posted;
        PostedUtc = DateTimeOffset.UtcNow;
    }
}
