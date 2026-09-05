using Aegis.Studio.Domain.Accounting;

namespace Aegis.Studio.Application.Abstractions;

public interface IAccountingGateway
{
    Task<AccountingPostResult> PostBatchAsync(AccountingBatch batch, CancellationToken cancellationToken = default);
}

public sealed record AccountingPostResult(bool Succeeded, string ExternalBatchReference, string ErrorMessage)
{
    public static AccountingPostResult Success(string reference) => new(true, reference, string.Empty);
    public static AccountingPostResult Failure(string errorMessage) => new(false, string.Empty, errorMessage);
}
