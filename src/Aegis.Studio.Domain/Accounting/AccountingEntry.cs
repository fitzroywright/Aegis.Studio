namespace Aegis.Studio.Domain.Accounting;

public sealed class AccountingEntry
{
    internal AccountingEntry(Guid batchId, string accountCode, decimal debit, decimal credit, string description, string sourceReference)
    {
        Id = Guid.NewGuid();
        BatchId = batchId;
        AccountCode = accountCode.Trim();
        Debit = debit;
        Credit = credit;
        Description = description.Trim();
        SourceReference = sourceReference.Trim();
    }

    public Guid Id { get; private set; }
    public Guid BatchId { get; private set; }
    public string AccountCode { get; private set; } = string.Empty;
    public decimal Debit { get; private set; }
    public decimal Credit { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public string SourceReference { get; private set; } = string.Empty;
}
