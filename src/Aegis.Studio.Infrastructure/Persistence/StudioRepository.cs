using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class StudioRepository : IBookingRepository, IStudioCatalogRepository, ICommunicationRepository
{
    private readonly StudioDbContext dbContext;

    public StudioRepository(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task AddAsync(Booking booking, CancellationToken cancellationToken) => await dbContext.Bookings.AddAsync(booking, cancellationToken);
    public async Task AddConversationAsync(Conversation conversation, CancellationToken cancellationToken) => await dbContext.Conversations.AddAsync(conversation, cancellationToken);
    public async Task AddMessageAsync(CommunicationMessage message, CancellationToken cancellationToken) => await dbContext.CommunicationMessages.AddAsync(message, cancellationToken);
    public async Task<ServiceOffering?> GetServiceAsync(Guid id, CancellationToken cancellationToken) => await dbContext.ServiceOfferings.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    public async Task<Photographer?> GetPhotographerAsync(Guid id, CancellationToken cancellationToken) => await dbContext.Photographers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    public async Task<IReadOnlyList<ServiceOffering>> GetServicesAsync(CancellationToken cancellationToken) => await dbContext.ServiceOfferings.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Photographer>> GetPhotographersAsync(CancellationToken cancellationToken) => await dbContext.Photographers.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<AvailabilityRule>> GetAvailabilityRulesAsync(Guid photographerId, DayOfWeek dayOfWeek, CancellationToken cancellationToken) => await dbContext.AvailabilityRules.AsNoTracking().Where(item => item.PhotographerId == photographerId && item.DayOfWeek == dayOfWeek).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<Booking>> GetForPhotographerAsync(Guid photographerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken) => await dbContext.Bookings.AsNoTracking().Where(item => item.PhotographerId == photographerId && item.StartUtc < toUtc && item.EndUtc > fromUtc).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Booking>> GetUpcomingAsync(CancellationToken cancellationToken)
    {
        List<Booking> bookings = await dbContext.Bookings
            .AsNoTracking()
            .Where(item => item.StartUtc >= DateTimeOffset.UtcNow && item.Status != BookingStatus.Cancelled)
            .ToListAsync(cancellationToken);

        return bookings
            .OrderBy(item => item.StartUtc)
            .Take(100)
            .ToList();
    }

    public async Task<Workspace?> GetWorkspaceByRecipientAsync(string recipientAddress, CancellationToken cancellationToken) => await dbContext.Workspaces.SingleOrDefaultAsync(item => item.PublicEmailAddress == recipientAddress.ToLower(), cancellationToken);
    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken) => await dbContext.Workspaces.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) => await dbContext.Conversations.SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);

    public async Task<Conversation?> FindActiveConversationAsync(Guid workspaceId, string clientEmail, string subject, CancellationToken cancellationToken)
    {
        string normalizedEmail = clientEmail.Trim().ToLowerInvariant();
        List<Conversation> conversations = await dbContext.Conversations
            .Where(item => item.WorkspaceId == workspaceId && item.ClientEmail == normalizedEmail && item.Subject == subject && item.Status != ConversationStatus.Closed)
            .ToListAsync(cancellationToken);

        return conversations
            .OrderByDescending(item => item.LastActivityUtc)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetWorkspaceConversationsAsync(Guid workspaceId, ConversationFolder folder, string? search, CancellationToken cancellationToken)
    {
        IQueryable<Conversation> query = dbContext.Conversations.AsNoTracking().Where(item => item.WorkspaceId == workspaceId);
        query = ApplyFolder(query, folder);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim().ToLower();
            query = query.Where(item => item.Subject.ToLower().Contains(term) || item.ClientEmail.ToLower().Contains(term) || (item.AssignedUserId != null && item.AssignedUserId.ToLower().Contains(term)));
        }

        List<Conversation> filteredConversations = await query.ToListAsync(cancellationToken);
        List<Conversation> conversations = filteredConversations
            .OrderByDescending(item => item.LastActivityUtc)
            .Take(200)
            .ToList();

        List<ConversationSummary> summaries = new();
        foreach (Conversation conversation in conversations)
        {
            List<CommunicationMessage> messages = await dbContext.CommunicationMessages
                .AsNoTracking()
                .Where(item => item.ConversationId == conversation.Id)
                .ToListAsync(cancellationToken);

            CommunicationMessage? lastMessage = messages
                .OrderByDescending(item => item.CreatedUtc)
                .FirstOrDefault();

            string preview = lastMessage?.Body ?? string.Empty;
            if (preview.Length > 120)
            {
                preview = preview[..120] + "…";
            }

            summaries.Add(new ConversationSummary(conversation.Id, conversation.WorkspaceId, conversation.BookingId, conversation.ClientEmail, conversation.Subject, conversation.Status, conversation.IsUnread, conversation.AssignedUserId, conversation.LastActivityUtc, preview, lastMessage?.Direction ?? MessageDirection.Internal));
        }

        return summaries;
    }

    public async Task<IReadOnlyList<CommunicationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        List<CommunicationMessage> messages = await dbContext.CommunicationMessages
            .AsNoTracking()
            .Where(item => item.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        return messages
            .OrderBy(item => item.CreatedUtc)
            .ToList();
    }

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
