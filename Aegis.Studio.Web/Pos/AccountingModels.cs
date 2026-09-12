using Aegis.Pos.Core;

namespace Aegis.Studio.Web.Pos;

public sealed record OpenRegisterRequest(
    string TerminalId,
    decimal OpeningFloat);

public sealed record CloseRegisterRequest(
    decimal CountedCash,
    string? Note);

public sealed record RegisterSession(
    Guid Id,
    string TerminalId,
    string CashierUserId,
    decimal OpeningFloat,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset? ClosedAtUtc,
    decimal? CountedCash,
    decimal? ExpectedCash,
    decimal? Variance,
    string Status);

public sealed record TenderReconciliation(
    TenderKind Kind,
    decimal SalesAmount,
    decimal RefundAmount,
    decimal NetAmount);

public sealed record ReconciliationSnapshot(
    Guid RegisterSessionId,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    decimal OpeningFloat,
    decimal GrossSales,
    decimal RefundTotal,
    decimal NetSales,
    decimal ExpectedCash,
    decimal? CountedCash,
    decimal? CashVariance,
    IReadOnlyList<TenderReconciliation> Tenders);

public sealed record RegisterCloseResult(
    RegisterSession Session,
    ReconciliationSnapshot Reconciliation);

public sealed record AccountMapping(
    string Key,
    string AccountCode,
    string? Description);

public sealed record AccountingBatchLine(
    Guid Id,
    string AccountCode,
    string Description,
    decimal Debit,
    decimal Credit);

public sealed record AccountingBatch(
    Guid Id,
    string Reference,
    Guid RegisterSessionId,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    DateTimeOffset CreatedAtUtc,
    string Status,
    decimal DebitTotal,
    decimal CreditTotal,
    IReadOnlyList<AccountingBatchLine> Lines,
    DateTimeOffset? ExportedAtUtc,
    string? ExternalReference);

public sealed record AccountingExportResult(
    bool Success,
    string Status,
    string? ExternalReference,
    string Message);
