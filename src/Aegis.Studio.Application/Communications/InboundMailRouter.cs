using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Communications;

public sealed class InboundMailRouter
{
    private const string GeneralWorkspaceSlug = "general";

    private readonly ICommunicationRepository communicationRepository;
    private readonly CommunicationService communicationService;

    public InboundMailRouter(ICommunicationRepository communicationRepository, CommunicationService communicationService)
    {
        this.communicationRepository = communicationRepository;
        this.communicationService = communicationService;
    }

    public async Task<InboundMailRouteResult> RouteAsync(InboundMailItem inboundMail, CancellationToken cancellationToken)
    {
        Workspace workspace = await ResolveWorkspaceAsync(inboundMail.OriginalRecipientAddress, cancellationToken);
        Conversation conversation = await communicationService.RouteInboundAsync(inboundMail, workspace, cancellationToken);

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

    private async Task<Workspace> ResolveWorkspaceAsync(string? originalRecipientAddress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(originalRecipientAddress))
        {
            Workspace? mappedWorkspace = await communicationRepository.GetWorkspaceByRecipientAsync(originalRecipientAddress, cancellationToken);
            if (mappedWorkspace is not null)
            {
                return mappedWorkspace;
            }
        }

        IReadOnlyList<Workspace> workspaces = await communicationRepository.GetWorkspacesAsync(cancellationToken);
        Workspace? generalWorkspace = workspaces.SingleOrDefault(item => item.Slug.Equals(GeneralWorkspaceSlug, StringComparison.OrdinalIgnoreCase));
        return generalWorkspace ?? throw new InvalidOperationException("No General workspace is configured for unroutable inbound mail.");
    }
}
