using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Communications;

namespace Aegis.Studio.Application.Workspaces;

public sealed class WorkspaceDashboardService
{
    private readonly ICommunicationRepository communicationRepository;
    private readonly IBookingRepository bookingRepository;
    private readonly IOperationsRepository operationsRepository;

    public WorkspaceDashboardService(ICommunicationRepository communicationRepository, IBookingRepository bookingRepository, IOperationsRepository operationsRepository)
    {
        this.communicationRepository = communicationRepository;
        this.bookingRepository = bookingRepository;
        this.operationsRepository = operationsRepository;
    }

    public async Task<WorkspaceDashboard> GetAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationSummary> inbox = await communicationRepository.GetWorkspaceConversationsAsync(workspaceId, ConversationFolder.Inbox, null, cancellationToken);
        IReadOnlyList<ConversationSummary> open = await communicationRepository.GetWorkspaceConversationsAsync(workspaceId, ConversationFolder.Open, null, cancellationToken);
        IReadOnlyList<ConversationSummary> waiting = await communicationRepository.GetWorkspaceConversationsAsync(workspaceId, ConversationFolder.Waiting, null, cancellationToken);
        int upcoming = (await bookingRepository.GetForWorkspaceAsync(workspaceId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), cancellationToken)).Count;
        int alerts = (await operationsRepository.GetOpenAlertsAsync(workspaceId, cancellationToken)).Count;
        return new WorkspaceDashboard(inbox.Count, open.Count, waiting.Count, upcoming, alerts);
    }
}
