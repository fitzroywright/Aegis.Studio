using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class NotificationRepository : INotificationRepository
{
    private readonly StudioDbContext dbContext;

    public NotificationRepository(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<NotificationJob>> GetDueAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        List<NotificationJob> pending = await dbContext.NotificationJobs.Where(item => item.Status == NotificationStatus.Pending).ToListAsync(cancellationToken);
        return pending.Where(item => item.DueUtc <= dueUtc).OrderBy(item => item.DueUtc).Take(100).ToList();
    }

    public async Task<IReadOnlyList<NotificationJob>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) => await dbContext.NotificationJobs.AsNoTracking().Where(item => item.WorkspaceId == workspaceId).OrderByDescending(item => item.CreatedUtc).Take(200).ToListAsync(cancellationToken);
    public async Task AddAsync(NotificationJob notification, CancellationToken cancellationToken) => await dbContext.NotificationJobs.AddAsync(notification, cancellationToken);
}
