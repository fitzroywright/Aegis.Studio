using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Pos;

public sealed class PosCatalogStore
{
    private const string Collection = "pos-catalog";
    private readonly PostgresDocumentStore persistence;

    public PosCatalogStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public IReadOnlyList<PosCatalogItem> GetAll(bool includeInactive = false)
        => persistence.GetAll<PosCatalogItem>(Collection)
            .Where(item => includeInactive || item.IsActive)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Name)
            .ToList();

    public PosCatalogItem? Get(Guid id) => persistence.Get<PosCatalogItem>(Collection, id);

    public PosCatalogItem Save(PosCatalogItemRequest request, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Item name is required.");
        }
        if (request.UnitPrice < 0m)
        {
            throw new InvalidOperationException("Unit price cannot be negative.");
        }

        Guid itemId = id ?? Guid.NewGuid();
        PosCatalogItem item = new
        (
            itemId,
            request.Code?.Trim() ?? string.Empty,
            request.Name.Trim(),
            string.IsNullOrWhiteSpace(request.Category) ? "General" : request.Category.Trim(),
            request.UnitPrice,
            request.IsActive,
            request.SortOrder,
            DateTimeOffset.UtcNow
        );
        persistence.Upsert(Collection, item.Id, item);
        return item;
    }
}

public sealed record PosCatalogItem
(
    Guid Id,
    string Code,
    string Name,
    string Category,
    decimal UnitPrice,
    bool IsActive,
    int SortOrder,
    DateTimeOffset UpdatedAtUtc
);

public sealed record PosCatalogItemRequest
(
    string? Code,
    string Name,
    string? Category,
    decimal UnitPrice,
    bool IsActive = true,
    int SortOrder = 0
);
