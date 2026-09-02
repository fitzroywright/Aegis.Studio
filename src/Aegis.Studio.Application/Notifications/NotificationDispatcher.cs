using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Notifications;

namespace Aegis.Studio.Application.Notifications;

public sealed class NotificationDispatcher
{
    private readonly INotificationRepository notificationRepository;
    private readonly INotificationChannel notificationChannel;
    private readonly IUnitOfWork unitOfWork;

    public NotificationDispatcher(INotificationRepository notificationRepository, INotificationChannel notificationChannel, IUnitOfWork unitOfWork)
    {
        this.notificationRepository = notificationRepository;
        this.notificationChannel = notificationChannel;
        this.unitOfWork = unitOfWork;
    }

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<NotificationJob> due = await notificationRepository.GetDueAsync(DateTimeOffset.UtcNow, cancellationToken);
        int delivered = 0;
        foreach (NotificationJob notification in due)
        {
            IntegrationResult result = await notificationChannel.SendAsync(notification, cancellationToken);
            if (result.Succeeded)
            {
                notification.MarkDelivered();
                delivered++;
            }
            else
            {
                notification.MarkFailed(result.Message ?? "Notification channel is unavailable.");
            }
        }

        if (due.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return delivered;
    }
}
