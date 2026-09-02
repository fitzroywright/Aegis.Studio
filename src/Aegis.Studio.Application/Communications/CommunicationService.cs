using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed class CommunicationService
{
    private readonly ICommunicationRepository communicationRepository;
    private readonly IOutboundMailProvider outboundMailProvider;
    private readonly IUnitOfWork unitOfWork;

    public CommunicationService(ICommunicationRepository communicationRepository, IOutboundMailProvider outboundMailProvider, IUnitOfWork unitOfWork)
    {
        this.communicationRepository = communicationRepository;
        this.outboundMailProvider = outboundMailProvider;
        this.unitOfWork = unitOfWork;
    }

    public async Task<Conversation> RouteInboundAsync(InboundMailItem inboundMail, CancellationToken cancellationToken)
    {
        Workspace workspace = await communicationRepository.GetWorkspaceByRecipientAsync(inboundMail.OriginalRecipientAddress, cancellationToken)
            ?? throw new InvalidOperationException($"No Workspace is configured for {inboundMail.OriginalRecipientAddress}.");

        Conversation? existingConversation = await communicationRepository.FindActiveConversationAsync(workspace.Id, inboundMail.FromAddress, NormalizeSubject(inboundMail.Subject), cancellationToken);
        Conversation conversation = existingConversation ?? new Conversation(workspace.Id, null, inboundMail.FromAddress, NormalizeSubject(inboundMail.Subject));
        if (existingConversation is null)
        {
            await communicationRepository.AddConversationAsync(conversation, cancellationToken);
        }
        else
        {
            conversation.ReceiveInbound();
        }

        CommunicationMessage message = new(conversation.Id, MessageDirection.Inbound, inboundMail.FromAddress, workspace.PublicEmailAddress, inboundMail.Subject, inboundMail.Body, inboundMail.InternetMessageId);
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task ReplyAsync(Guid conversationId, string body, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        Workspace workspace = await GetWorkspaceAsync(conversation.WorkspaceId, cancellationToken);
        string subject = conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : $"Re: {conversation.Subject}";

        await outboundMailProvider.SendAsync(workspace.PublicEmailAddress, conversation.ClientEmail, subject, body, cancellationToken);
        CommunicationMessage message = new(conversation.Id, MessageDirection.Outbound, workspace.PublicEmailAddress, conversation.ClientEmail, subject, body);
        conversation.MarkWaitingForCustomer();
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task AddInternalNoteAsync(Guid conversationId, string body, string author, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        CommunicationMessage message = new(conversation.Id, MessageDirection.Internal, author, "internal", conversation.Subject, body);
        conversation.Touch();
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkReadAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        conversation.MarkRead();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task AssignAsync(Guid conversationId, string? userId, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        conversation.AssignTo(userId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid conversationId, ConversationStatus status, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        switch (status)
        {
            case ConversationStatus.New:
            case ConversationStatus.Open:
                conversation.Reopen();
                break;
            case ConversationStatus.WaitingForCustomer:
                conversation.MarkWaitingForCustomer();
                break;
            case ConversationStatus.Resolved:
                conversation.Resolve();
                break;
            case ConversationStatus.Closed:
                conversation.Close();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConversationDetails> GetDetailsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        return new ConversationDetails(conversation.Id, conversation.WorkspaceId, conversation.BookingId, conversation.ClientEmail, conversation.Subject, conversation.Status, conversation.IsUnread, conversation.AssignedUserId, conversation.CreatedUtc, conversation.LastActivityUtc);
    }

    private async Task<Conversation> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        return await communicationRepository.GetConversationAsync(conversationId, cancellationToken) ?? throw new InvalidOperationException("Conversation was not found.");
    }

    private async Task<Workspace> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Workspace> workspaces = await communicationRepository.GetWorkspacesAsync(cancellationToken);
        return workspaces.Single(item => item.Id == workspaceId);
    }

    private static string NormalizeSubject(string subject)
    {
        string normalized = subject.Trim();
        while (normalized.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
        {
            int separatorIndex = normalized.IndexOf(':');
            normalized = normalized[(separatorIndex + 1)..].Trim();
        }

        return normalized;
    }
}
