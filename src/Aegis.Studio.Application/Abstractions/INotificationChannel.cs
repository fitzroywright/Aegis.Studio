using Aegis.Studio.Domain.Notifications;

namespace Aegis.Studio.Application.Abstractions;

public interface INotificationChannel
{
    Task<IntegrationResult> SendAsync(NotificationJob notification, CancellationToken cancellationToken);
}
