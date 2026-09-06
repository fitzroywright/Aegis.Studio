using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Email;

public sealed class UnroutedEmailStore
{
    private const string Collection = "unrouted-email";
    private readonly PostgresDocumentStore persistence;

    public UnroutedEmailStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public IReadOnlyList<UnroutedEmail> GetOpen()
        => persistence.GetAll<UnroutedEmail>(Collection).Where(x => !x.ResolvedAt.HasValue).OrderByDescending(x => x.ReceivedAt).ToList();

    public UnroutedEmail Capture(InboundEmail email, string reason)
    {
        UnroutedEmail? existing = persistence.GetAll<UnroutedEmail>(Collection)
            .FirstOrDefault(x => string.Equals(x.ProviderMessageId, email.MessageId, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        UnroutedEmail item = new(Guid.NewGuid(), email.MessageId, email.ThreadId, email.FromName, email.FromAddress,
            email.ToAddresses.ToList(), email.CcAddresses.ToList(), new Dictionary<string, string>(email.InternetHeaders, StringComparer.OrdinalIgnoreCase),
            email.Subject, email.Body, email.ReceivedAt.ToUniversalTime(), reason, null, null, null);
        persistence.Upsert(Collection, item.Id, item);
        return item;
    }

    public UnroutedEmail? Get(Guid id) => persistence.Get<UnroutedEmail>(Collection, id);

    public UnroutedEmail? MarkResolved(Guid id, Guid workspaceId, string workspaceName)
    {
        UnroutedEmail? current = Get(id);
        if (current is null)
        {
            return null;
        }

        UnroutedEmail updated = current with { ResolvedAt = DateTimeOffset.UtcNow, ResolvedWorkspaceId = workspaceId, ResolvedWorkspaceName = workspaceName };
        persistence.Upsert(Collection, updated.Id, updated);
        return updated;
    }
}

public sealed record UnroutedEmail(Guid Id, string ProviderMessageId, string? ThreadId, string FromName, string FromAddress, List<string> ToAddresses, List<string> CcAddresses, Dictionary<string, string> InternetHeaders, string Subject, string Body, DateTimeOffset ReceivedAt, string FailureReason, DateTimeOffset? ResolvedAt, Guid? ResolvedWorkspaceId, string? ResolvedWorkspaceName)
{
    public InboundEmail ToInboundEmail() => new(ProviderMessageId, ThreadId, FromName, FromAddress, ToAddresses, CcAddresses, InternetHeaders, Subject, Body, ReceivedAt);
}

public sealed record ResolveUnroutedEmailRequest(Guid WorkspaceId);
