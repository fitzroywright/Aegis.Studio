using System.Text.RegularExpressions;
using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed partial class CommunicationService
{
    private readonly ICommunicationRepository communicationRepository;
    private readonly IBookingRepository bookingRepository;
    private readonly IOutboundMailProvider outboundMailProvider;
    private readonly IUnitOfWork unitOfWork;

    public CommunicationService(ICommunicationRepository communicationRepository, IBookingRepository bookingRepository, IOutboundMailProvider outboundMailProvider, IUnitOfWork unitOfWork)
    {
        this.communicationRepository = communicationRepository;
        this.bookingRepository = bookingRepository;
        this.outboundMailProvider = outboundMailProvider;
        this.unitOfWork = unitOfWork;
    }

    public async Task<Conversation> RouteInboundAsync(InboundMailItem inboundMail, CancellationToken cancellationToken)
    {
        Workspace workspace = await communicationRepository.GetWorkspaceByRecipientAsync(inboundMail.OriginalRecipientAddress, cancellationToken)
            ?? throw new InvalidOperationException($"No Workspace is configured for {inboundMail.OriginalRecipientAddress}.");

        return await RouteInboundAsync(inboundMail, workspace, cancellationToken);
    }

    public async Task<Conversation> RouteInboundAsync(InboundMailItem inboundMail, Workspace workspace, CancellationToken cancellationToken)
    {
        string normalizedSubject = NormalizeSubject(inboundMail.Subject);
        Booking? booking = await FindBookingAsync(workspace.Id, inboundMail.FromAddress, inboundMail.Subject, cancellationToken);
        Conversation? existingConversation = await communicationRepository.FindActiveConversationAsync(workspace.Id, inboundMail.FromAddress, normalizedSubject, cancellationToken);
        Conversation conversation = existingConversation ?? new Conversation(workspace.Id, booking?.Id, inboundMail.FromAddress, normalizedSubject);
        if (existingConversation is null)
        {
            await communicationRepository.AddConversationAsync(conversation, cancellationToken);
        }
        else
        {
            conversation.ReceiveInbound();
            if (conversation.BookingId is null && booking is not null)
            {
                conversation.LinkBooking(booking.Id);
            }
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
        if (status == ConversationStatus.WaitingForCustomer) conversation.MarkWaitingForCustomer();
        else if (status == ConversationStatus.Resolved) conversation.Resolve();
        else if (status == ConversationStatus.Closed) conversation.Close();
        else conversation.Reopen();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConversationDetails> GetDetailsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        Conversation conversation = await GetConversationAsync(conversationId, cancellationToken);
        return new ConversationDetails(conversation.Id, conversation.WorkspaceId, conversation.BookingId, conversation.ClientEmail, conversation.Subject, conversation.Status, conversation.IsUnread, conversation.AssignedUserId, conversation.CreatedUtc, conversation.LastActivityUtc);
    }

    private async Task<Booking?> FindBookingAsync(Guid workspaceId, string clientEmail, string subject, CancellationToken cancellationToken)
    {
        Match match = BookingReferenceRegex().Match(subject);
        if (match.Success)
        {
            Booking? byReference = await bookingRepository.FindByReferenceAsync(match.Value.ToUpperInvariant(), cancellationToken);
            if (byReference is not null)
            {
                return byReference;
            }
        }

        return await bookingRepository.FindActiveByClientAsync(workspaceId, clientEmail, cancellationToken);
    }

    private async Task<Conversation> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) => await communicationRepository.GetConversationAsync(conversationId, cancellationToken) ?? throw new InvalidOperationException("Conversation was not found.");
    private async Task<Workspace> GetWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) => (await communicationRepository.GetWorkspacesAsync(cancellationToken)).Single(item => item.Id == workspaceId);

    private static string NormalizeSubject(string subject)
    {
        string normalized = subject.Trim();
        while (normalized.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[(normalized.IndexOf(':') + 1)..].Trim();
        }
        return normalized;
    }

    [GeneratedRegex("BK-\\d{8}-\\d{4}", RegexOptions.IgnoreCase)]
    private static partial Regex BookingReferenceRegex();
}
