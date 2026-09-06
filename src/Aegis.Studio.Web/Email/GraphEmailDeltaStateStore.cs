using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Email;

public sealed class GraphEmailDeltaStateStore
{
    private const string DeltaKey = "graph-email-delta-link";
    private readonly PostgresDocumentStore persistence;

    public GraphEmailDeltaStateStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public string? GetDeltaLink() => persistence.GetState(DeltaKey);

    public void SaveDeltaLink(string deltaLink)
    {
        if (string.IsNullOrWhiteSpace(deltaLink))
        {
            throw new ArgumentException("Delta link is required.", nameof(deltaLink));
        }
        persistence.SetState(DeltaKey, deltaLink.Trim());
    }

    public void Clear() => persistence.DeleteState(DeltaKey);
}

public sealed record GraphEmailDeltaState(string DeltaLink, DateTimeOffset UpdatedAt);
