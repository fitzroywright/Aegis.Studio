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
        Workspace workspace = await communicationRepository.GetWorkspaceByRecipientAsync(inboundMail.OriginalRecipientAddress, cancellationToken) ?? throw new InvalidOperationException($"No Workspace is configured for {inboundMail.OriginalRecipientAddress}.");
        Conversation conversation = new(workspace.Id, null, inboundMail.FromAddress, inboundMail.Subject);
        CommunicationMessage message = new(conversation.Id, MessageDirection.Inbound, inboundMail.FromAddress, workspace.PublicEmailAddress, inboundMail.Subject, inboundMail.Body, inboundMail.InternetMessageId);
        await communicationRepository.AddConversationAsync(conversation, cancellationToken);
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task ReplyAsync(Guid conversationId, string body, CancellationToken cancellationToken)
    {
        Conversation conversation = await communicationRepository.GetConversationAsync(conversationId, cancellationToken) ?? throw new InvalidOperationException("Conversation was not found.");
        IReadOnlyList<Workspace> workspaces = await communicationRepository.GetWorkspacesAsync(cancellationToken);
        Workspace workspace = workspaces.Single(item => item.Id == conversation.WorkspaceId);
        string subject = conversation.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? conversation.Subject : $"Re: {conversation.Subject}";
        await outboundMailProvider.SendAsync(workspace.PublicEmailAddress, conversation.ClientEmail, subject, body, cancellationToken);
        CommunicationMessage message = new(conversation.Id, MessageDirection.Outbound, workspace.PublicEmailAddress, conversation.ClientEmail, subject, body);
        conversation.Touch();
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
