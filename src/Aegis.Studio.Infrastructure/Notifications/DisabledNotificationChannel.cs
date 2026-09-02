using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Notifications;

namespace Aegis.Studio.Infrastructure.Notifications;

public sealed class DisabledNotificationChannel : INotificationChannel
{
    public Task<IntegrationResult> SendAsync(NotificationJob notification, CancellationToken cancellationToken) => Task.FromResult(new IntegrationResult(false, false, null, $"{notification.Channel} delivery is not configured."));
}
