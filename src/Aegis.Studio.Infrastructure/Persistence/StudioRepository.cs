using Aegis.Studio.Application.Abstractions;
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
    public async Task<IReadOnlyList<Booking>> GetUpcomingAsync(CancellationToken cancellationToken) => await dbContext.Bookings.AsNoTracking().Where(item => item.StartUtc >= DateTimeOffset.UtcNow && item.Status != BookingStatus.Cancelled).OrderBy(item => item.StartUtc).Take(100).ToListAsync(cancellationToken);
    public async Task<Workspace?> GetWorkspaceByRecipientAsync(string recipientAddress, CancellationToken cancellationToken) => await dbContext.Workspaces.SingleOrDefaultAsync(item => item.PublicEmailAddress == recipientAddress.ToLower(), cancellationToken);
    public async Task<IReadOnlyList<Workspace>> GetWorkspacesAsync(CancellationToken cancellationToken) => await dbContext.Workspaces.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.Name).ToListAsync(cancellationToken);
    public async Task<Conversation?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken) => await dbContext.Conversations.SingleOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
    public async Task<IReadOnlyList<Conversation>> GetWorkspaceConversationsAsync(Guid workspaceId, CancellationToken cancellationToken) => await dbContext.Conversations.AsNoTracking().Where(item => item.WorkspaceId == workspaceId).OrderByDescending(item => item.LastActivityUtc).ToListAsync(cancellationToken);
    public async Task<IReadOnlyList<CommunicationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken) => await dbContext.CommunicationMessages.AsNoTracking().Where(item => item.ConversationId == conversationId).OrderBy(item => item.CreatedUtc).ToListAsync(cancellationToken);
}
