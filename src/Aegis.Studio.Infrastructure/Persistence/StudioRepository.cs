using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Operations;
using Aegis.Studio.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class StudioRepository : IBookingRepository, IStudioCatalogRepository, ICommunicationRepository, IWorkspaceRepository, IOperationsRepository
{
    private readonly StudioDbContext dbContext;

    public StudioRepository(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken) => await dbContext.Bookings.AddAsync(booking, cancellationToken);
    public async Task<Booking?> GetAsync(Guid bookingId, CancellationToken cancellationToken) => await dbContext.Bookings.SingleOrDefaultAsync(item => item.Id == bookingId, cancellationToken);
    public async Task<Booking?> FindByReferenceAsync(string reference, CancellationToken cancellationToken) => await dbContext.Bookings.SingleOrDefaultAsync(item => item.Reference == reference, cancellationToken);
    public async Task<Booking?> FindByAccessTokenAsync(string accessToken, CancellationToken cancellationToken) => await dbContext.Bookings.SingleOrDefaultAsync(item => item.AccessToken == accessToken, cancellationToken);

    public async Task<Booking?> FindActiveByClientAsync(Guid workspaceId, string clientEmail, CancellationToken cancellationToken)
    {
        string email = clientEmail.Trim().ToLowerInvariant();
        List<Booking> bookings = await dbContext.Bookings
            .Where(item => item.WorkspaceId == workspaceId && item.ClientEmail == email && item.Status != BookingStatus.Cancelled)
            .ToListAsync(cancellationToken);
        return bookings.OrderByDescending(item => item.StartUtc).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Booking>> GetForPhotographerAsync(Guid photographerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        List<Booking> bookings = await dbContext.Bookings.AsNoTracking()
            .Where(item => item.PhotographerId == photographerId)
            .ToListAsync(cancellationToken);
        return bookings.Where(item => item.StartUtc < toUtc && item.EndUtc > fromUtc).ToList();
    }

    public async Task<IReadOnlyList<Booking>> GetForWorkspaceAsync(Guid workspaceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        List<Booking> bookings = await dbContext.Bookings.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.Status != BookingStatus.Cancelled)
            .ToListAsync(cancellationToken);
        return bookings.Where(item => item.StartUtc < toUtc && item.EndUtc > fromUtc).ToList();
    }

    public async Task<IReadOnlyList<Booking>> GetUpcomingAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        List<Booking> bookings = await dbContext.Bookings.AsNoTracking()
            .Where(item => item.Status != BookingStatus.Cancelled)
            .ToListAsync(cancellationToken);
        return bookings
            .Where(item => item.StartUtc >= nowUtc)
            .OrderBy(item => item.StartUtc)
            .Take(100)
            .ToList();
    }

    public async Task<ServiceOffering?> GetServiceAsync(Guid id, CancellationToken cancellationToken) => await dbContext.ServiceOfferings.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    public async Task<Photographer?> GetPhotographerAsync(Guid id, CancellationToken cancellationToken) => await dbContext.Photographers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    public async Task<IReadOnlyList<ServiceOffering>> GetServicesAsync(CancellationToken cancellationToken) => await dbContext.ServiceOfferings.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Photographer>> GetPhotographersAsync(CancellationToken cancellationToken) => await dbContext.Photographers.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<AvailabilityRule>> GetAvailabilityRulesAsync(Guid photographerId, DayOfWeek dayOfWeek, CancellationToken cancellationToken) => await dbContext.AvailabilityRules.AsNoTracking().Where(item => item.PhotographerId == photographerId && item.DayOfWeek == dayOfWeek).ToListAsync(cancellationToken);
    public async Task<Workspace?> GetWorkspaceByRecipientAsync(string recipientAddress, CancellationToken cancellationToken) => await dbContext.Workspaces.SingleOrDefaultAsync(item => item.PublicEmailAddress == recipientAddress.Trim().ToLowerInvariant(), cancellationToken);
    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken) => await dbContext.Workspaces.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) => await dbContext.Conversations.SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);

    public async Task<Conversation?> FindActiveConversationAsync(Guid workspaceId, string clientEmail, string subject, CancellationToken cancellationToken)
    {
        string email = clientEmail.Trim().ToLowerInvariant();
        List<Conversation> conversations = await dbContext.Conversations.Where(item => item.WorkspaceId == workspaceId && item.ClientEmail == email && item.Subject == subject && item.Status != ConversationStatus.Closed).ToListAsync(cancellationToken);
        return conversations.OrderByDescending(item => item.LastActivityUtc).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetWorkspaceConversationsAsync(Guid workspaceId, ConversationFolder folder, string? search, CancellationToken cancellationToken)
    {
        IQueryable<Conversation> query = ApplyFolder(dbContext.Conversations.AsNoTracking().Where(item => item.WorkspaceId == workspaceId), folder);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLowerInvariant();
            query = query.Where(item => item.Subject.ToLower().Contains(term) || item.ClientEmail.ToLower().Contains(term) || (item.AssignedUserId != null && item.AssignedUserId.ToLower().Contains(term)));
        }

        List<Conversation> filtered = await query.ToListAsync(cancellationToken);
        List<Conversation> conversations = filtered.OrderByDescending(item => item.LastActivityUtc).Take(200).ToList();
        List<ConversationSummary> summaries = new();
        foreach (Conversation conversation in conversations)
        {
            List<CommunicationMessage> messages = await dbContext.CommunicationMessages.AsNoTracking().Where(item => item.ConversationId == conversation.Id).ToListAsync(cancellationToken);
            CommunicationMessage? lastMessage = messages.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
            string preview = lastMessage?.Body ?? string.Empty;
            if (preview.Length > 120) preview = preview[..120] + "…";
            summaries.Add(new ConversationSummary(conversation.Id, conversation.WorkspaceId, conversation.BookingId, conversation.ClientEmail, conversation.Subject, conversation.Status, conversation.IsUnread, conversation.AssignedUserId, conversation.LastActivityUtc, preview, lastMessage?.Direction ?? MessageDirection.Internal));
        }
        return summaries;
    }

    public async Task<IReadOnlyList<CommunicationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        List<CommunicationMessage> messages = await dbContext.CommunicationMessages.AsNoTracking().Where(item => item.ConversationId == conversationId).ToListAsync(cancellationToken);
        return messages.OrderBy(item => item.CreatedUtc).ToList();
    }

    public async Task<IReadOnlyList<Conversation>> GetUnansweredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        List<Conversation> candidates = await dbContext.Conversations.AsNoTracking().Where(item => item.Status != ConversationStatus.Closed && item.Status != ConversationStatus.Resolved && item.Status != ConversationStatus.WaitingForCustomer && item.LastCustomerMessageUtc != null).ToListAsync(cancellationToken);
        return candidates.Where(item => item.LastCustomerMessageUtc <= olderThanUtc && (item.LastStaffResponseUtc == null || item.LastStaffResponseUtc < item.LastCustomerMessageUtc)).ToList();
    }

    public async Task<bool> HasOpenEscalationAsync(Guid conversationId, string kind, CancellationToken cancellationToken) => await dbContext.OperationalAlerts.AnyAsync(item => item.ConversationId == conversationId && item.Kind == kind && !item.IsAcknowledged, cancellationToken);
    public async Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken) => await dbContext.Conversations.AddAsync(conversation, cancellationToken);
    public async Task AddMessageAsync(CommunicationMessage message, CancellationToken cancellationToken) => await dbContext.CommunicationMessages.AddAsync(message, cancellationToken);
    public async Task<IReadOnlyList<WorkspaceMember>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken) => await dbContext.WorkspaceMembers.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && item.IsActive).OrderBy(item => item.DisplayName).ToListAsync(cancellationToken);
    public async Task<WorkspaceMember?> GetMembershipAsync(Guid workspaceId, string userId, CancellationToken cancellationToken) => await dbContext.WorkspaceMembers.AsNoTracking().SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.UserId == userId && item.IsActive, cancellationToken);
    public async Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken) => await dbContext.WorkspaceMembers.AddAsync(member, cancellationToken);
    public async Task AddAlertAsync(OperationalAlert alert, CancellationToken cancellationToken) => await dbContext.OperationalAlerts.AddAsync(alert, cancellationToken);

    public async Task<IReadOnlyList<OperationalAlert>> GetOpenAlertsAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        List<OperationalAlert> alerts = await dbContext.OperationalAlerts.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && !item.IsAcknowledged).ToListAsync(cancellationToken);
        return alerts.OrderByDescending(item => item.CreatedUtc).ToList();
    }

    public async Task<OperationalAlert?> GetAlertAsync(Guid id, CancellationToken cancellationToken) => await dbContext.OperationalAlerts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    private IQueryable<Conversation> ApplyFolder(IQueryable<Conversation> query, ConversationFolder folder)
    {
        return folder switch
        {
            ConversationFolder.Inbox => query.Where(item => item.Status == ConversationStatus.New || item.IsUnread),
            ConversationFolder.Open => query.Where(item => item.Status == ConversationStatus.Open || item.Status == ConversationStatus.New),
            ConversationFolder.Waiting => query.Where(item => item.Status == ConversationStatus.WaitingForCustomer),
            ConversationFolder.Sent => query.Where(item => dbContext.CommunicationMessages.Any(message => message.ConversationId == item.Id && message.Direction == MessageDirection.Outbound)),
            ConversationFolder.Resolved => query.Where(item => item.Status == ConversationStatus.Resolved),
            ConversationFolder.Archived => query.Where(item => item.Status == ConversationStatus.Closed),
            _ => query
        };
    }
}
