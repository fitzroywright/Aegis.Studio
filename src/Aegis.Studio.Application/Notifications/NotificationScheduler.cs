using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Notifications;

namespace Aegis.Studio.Application.Notifications;

public sealed class NotificationScheduler
{
    private readonly INotificationRepository notificationRepository;
    private readonly IUnitOfWork unitOfWork;

    public NotificationScheduler(INotificationRepository notificationRepository, IUnitOfWork unitOfWork)
    {
        this.notificationRepository = notificationRepository;
        this.unitOfWork = unitOfWork;
    }

    public async Task ScheduleAsync(Guid workspaceId, Guid? bookingId, string channel, string recipient, string subject, string body, DateTimeOffset dueUtc, CancellationToken cancellationToken)
    {
        NotificationJob notification = new(workspaceId, bookingId, channel, recipient, subject, body, dueUtc);
        await notificationRepository.AddAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
