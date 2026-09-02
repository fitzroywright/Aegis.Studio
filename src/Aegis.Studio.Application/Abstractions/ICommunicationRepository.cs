using Aegis.Studio.Application.Communications;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Abstractions;

public interface ICommunicationRepository
{
    Task<Workspace?> GetWorkspaceByRecipientAsync(string recipientAddress, CancellationToken cancellationToken);
    Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken);
    Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken);
    Task<Conversation?> FindActiveConversationAsync(Guid workspaceId, string clientEmail, string subject, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSummary>> GetWorkspaceConversationsAsync(Guid workspaceId, ConversationFolder folder, string? search, CancellationToken cancellationToken);
    Task<IReadOnlyList<CommunicationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken);
    Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken);
    Task AddMessageAsync(CommunicationMessage message, CancellationToken cancellationToken);
}
