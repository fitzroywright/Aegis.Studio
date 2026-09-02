using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Notifications;
using Aegis.Studio.Application.Tasks;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class BookingLifecycleService
{
    private readonly NotificationScheduler notificationScheduler;
    private readonly TaskService taskService;
    private readonly ICalendarProvider calendarProvider;

    public BookingLifecycleService(NotificationScheduler notificationScheduler, TaskService taskService, ICalendarProvider calendarProvider)
    {
        this.notificationScheduler = notificationScheduler;
        this.taskService = taskService;
        this.calendarProvider = calendarProvider;
    }

    public async Task BookingCreatedAsync(Booking booking, CancellationToken cancellationToken)
    {
        await notificationScheduler.ScheduleAsync(booking.WorkspaceId, booking.Id, "Email", booking.ClientEmail, $"Booking confirmed - {booking.Reference}", $"Your Bryan Studios booking {booking.Reference} is confirmed for {booking.StartUtc:g}.", DateTimeOffset.UtcNow, cancellationToken);
        DateTimeOffset reminderDue = booking.StartUtc.AddHours(-24);
        if (reminderDue > DateTimeOffset.UtcNow)
        {
            await notificationScheduler.ScheduleAsync(booking.WorkspaceId, booking.Id, "Email", booking.ClientEmail, $"Reminder - {booking.Reference}", $"Reminder: your Bryan Studios session is scheduled for {booking.StartUtc:g}.", reminderDue, cancellationToken);
        }

        await taskService.CreateAsync(booking.WorkspaceId, booking.Id, null, "Review new booking", $"Review booking {booking.Reference} and confirm operational details.", null, DateTimeOffset.UtcNow.AddHours(2), cancellationToken);
        await calendarProvider.UpsertBookingAsync(booking, cancellationToken);
    }

    public async Task BookingRescheduledAsync(Booking booking, CancellationToken cancellationToken)
    {
        await notificationScheduler.ScheduleAsync(booking.WorkspaceId, booking.Id, "Email", booking.ClientEmail, $"Booking rescheduled - {booking.Reference}", $"Your booking has been rescheduled to {booking.StartUtc:g}.", DateTimeOffset.UtcNow, cancellationToken);
        await calendarProvider.UpsertBookingAsync(booking, cancellationToken);
    }

    public async Task BookingCancelledAsync(Booking booking, CancellationToken cancellationToken)
    {
        await notificationScheduler.ScheduleAsync(booking.WorkspaceId, booking.Id, "Email", booking.ClientEmail, $"Booking cancelled - {booking.Reference}", $"Your Bryan Studios booking {booking.Reference} has been cancelled.", DateTimeOffset.UtcNow, cancellationToken);
        await calendarProvider.DeleteBookingAsync(booking, cancellationToken);
    }

    public async Task BookingCompletedAsync(Booking booking, CancellationToken cancellationToken)
    {
        await taskService.CreateAsync(booking.WorkspaceId, booking.Id, null, "Prepare client delivery", $"Prepare post-session delivery for {booking.Reference}.", null, DateTimeOffset.UtcNow.AddDays(1), cancellationToken);
    }
}
