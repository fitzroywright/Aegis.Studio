using Aegis.Studio.Domain.Notifications;

namespace Aegis.Studio.Application.Abstractions;

public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationJob>> GetDueAsync(DateTimeOffset dueUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationJob>> GetForWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken);
    Task AddAsync(NotificationJob notification, CancellationToken cancellationToken);
}
