using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Pos;

public sealed class PosAuditStore
{
    private const string Collection = "pos-audit";
    private readonly PostgresDocumentStore persistence;

    public PosAuditStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public void Record(string action, Guid saleId, Guid jobId, string userId, decimal amount, string? detail = null)
    {
        PosAuditEntry entry = new
        (
            Guid.NewGuid(),
            action,
            saleId,
            jobId,
            userId,
            amount,
            detail,
            DateTimeOffset.UtcNow
        );
        persistence.Upsert(Collection, entry.Id, entry);
    }

    public IReadOnlyList<PosAuditEntry> GetRecent(int count = 100)
        => persistence.GetAll<PosAuditEntry>(Collection)
            .OrderByDescending(entry => entry.CreatedAtUtc)
            .Take(Math.Clamp(count, 1, 500))
            .ToList();
}

public sealed record PosAuditEntry
(
    Guid Id,
    string Action,
    Guid SaleId,
    Guid JobId,
    string UserId,
    decimal Amount,
    string? Detail,
    DateTimeOffset CreatedAtUtc
);
