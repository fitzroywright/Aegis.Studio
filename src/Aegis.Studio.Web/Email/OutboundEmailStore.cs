using Aegis.Studio.Web.Demo;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Email;

public sealed class OutboundEmailStore
{
    private const string Collection = "outbound-email";
    private readonly PostgresDocumentStore persistence;
    private readonly IReadOnlyDictionary<Guid, string> workspaceAddresses;

    public OutboundEmailStore(PostgresDocumentStore persistence, IConfiguration configuration)
    {
        this.persistence = persistence;
        List<WorkspaceEmailRoute> routes = configuration.GetSection("EmailRouting:Routes").Get<List<WorkspaceEmailRoute>>() ?? [];
        workspaceAddresses = routes.Where(x => x.WorkspaceId != Guid.Empty && !string.IsNullOrWhiteSpace(x.Address))
            .ToDictionary(x => x.WorkspaceId, x => x.Address, EqualityComparer<Guid>.Default);
    }

    public OutboundEmail Queue(DemoConversation conversation, string body)
        => QueueMessage(conversation, body, conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : "Re: " + conversation.Subject);

    public OutboundEmail? QueueAcknowledgement(DemoConversation conversation, string workspaceName)
    {
        bool alreadyQueued = persistence.GetAll<OutboundEmail>(Collection).Any(x => x.ConversationId == conversation.Id && string.Equals(x.Kind, "Acknowledgement", StringComparison.OrdinalIgnoreCase));
        if (alreadyQueued)
        {
            return null;
        }

        string jobReference = "AEGIS-" + conversation.Id.ToString("N")[..8].ToUpperInvariant();
        string body = $"Hello {conversation.ContactName},\n\nWe received your message and it has been accepted into the {workspaceName} workspace. Our team will handle it from here.\n\nReference: {jobReference}\nPlease keep this reference in the subject when starting a separate email about the same request.\n\nBryan's Studio";
        string subject = $"[{jobReference}] We received your message - {conversation.Subject}";
        return QueueMessage(conversation, body, subject, "Acknowledgement");
    }

    public IReadOnlyList<OutboundEmail> GetPending()
        => persistence.GetAll<OutboundEmail>(Collection).Where(x => string.Equals(x.Status, "Pending", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.CreatedAt).ToList();

    public OutboundEmail? MarkSent(Guid id, string? providerMessageId)
    {
        OutboundEmail? current = persistence.Get<OutboundEmail>(Collection, id);
        if (current is null)
        {
            return null;
        }
        OutboundEmail sent = current with { Status = "Sent", ProviderMessageId = string.IsNullOrWhiteSpace(providerMessageId) ? null : providerMessageId.Trim() };
        persistence.Upsert(Collection, sent.Id, sent);
        return sent;
    }

    private OutboundEmail QueueMessage(DemoConversation conversation, string body, string subject, string kind = "Reply")
    {
        if (!workspaceAddresses.TryGetValue(conversation.WorkspaceId, out string? fromAddress) || string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new InvalidOperationException("The conversation workspace has no outbound email identity configured.");
        }

        OutboundEmail message = new(Guid.NewGuid(), conversation.Id, fromAddress, conversation.ContactAddress, subject, body.Trim(), conversation.ExternalThreadId, "Pending", DateTimeOffset.UtcNow, null, kind);
        persistence.Upsert(Collection, message.Id, message);
        return message;
    }
}

public sealed record OutboundEmail(Guid Id, Guid ConversationId, string FromAddress, string ToAddress, string Subject, string Body, string? ThreadId, string Status, DateTimeOffset CreatedAt, string? ProviderMessageId, string Kind = "Reply");
public sealed record MarkOutboundSentRequest(string? ProviderMessageId);
