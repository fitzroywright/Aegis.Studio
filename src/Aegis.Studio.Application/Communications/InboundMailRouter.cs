using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed class InboundMailRouter
{
    private readonly ICommunicationRepository communicationRepository;
    private readonly CommunicationService communicationService;

    public InboundMailRouter(ICommunicationRepository communicationRepository, CommunicationService communicationService)
    {
        this.communicationRepository = communicationRepository;
        this.communicationService = communicationService;
    }

    public async Task<InboundMailRouteResult> RouteAsync(InboundMailItem inboundMail, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inboundMail.OriginalRecipientAddress))
        {
            throw new InvalidOperationException("Inbound message does not contain the original recipient address required for workspace routing.");
        }

        Workspace workspace = await communicationRepository.GetWorkspaceByRecipientAsync(inboundMail.OriginalRecipientAddress, cancellationToken)
            ?? throw new InvalidOperationException($"No workspace is configured for recipient {inboundMail.OriginalRecipientAddress}.");

        Conversation conversation = await communicationService.RouteInboundAsync(inboundMail, cancellationToken);

        return new InboundMailRouteResult
        (
            workspace.Id,
            workspace.Name,
            workspace.PublicEmailAddress,
            conversation.Id,
            conversation.BookingId,
            conversation.ClientEmail,
            conversation.Subject
        );
    }
}
