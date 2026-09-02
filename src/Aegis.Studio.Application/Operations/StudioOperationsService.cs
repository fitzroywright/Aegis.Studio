using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Customers;
using Aegis.Studio.Domain.Notifications;
using Aegis.Studio.Domain.Operations;
using Aegis.Studio.Domain.Tasks;

namespace Aegis.Studio.Application.Operations;

public sealed class StudioOperationsService
{
    private readonly IBookingRepository bookingRepository;
    private readonly ICustomerRepository customerRepository;
    private readonly ITaskRepository taskRepository;
    private readonly INotificationRepository notificationRepository;
    private readonly IOperationsRepository operationsRepository;
    private readonly ICommunicationRepository communicationRepository;

    public StudioOperationsService(IBookingRepository bookingRepository, ICustomerRepository customerRepository, ITaskRepository taskRepository, INotificationRepository notificationRepository, IOperationsRepository operationsRepository, ICommunicationRepository communicationRepository)
    {
        this.bookingRepository = bookingRepository;
        this.customerRepository = customerRepository;
        this.taskRepository = taskRepository;
        this.notificationRepository = notificationRepository;
        this.operationsRepository = operationsRepository;
        this.communicationRepository = communicationRepository;
    }

    public async Task<StudioOperationsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Workspace> workspaces = await communicationRepository.GetWorkspacesAsync(cancellationToken);
        IReadOnlyList<Booking> bookings = await bookingRepository.GetUpcomingAsync(cancellationToken);
        IReadOnlyList<Customer> customers = await customerRepository.SearchAsync(null, cancellationToken);
        List<StudioTask> tasks = new();
        List<NotificationJob> notifications = new();
        List<OperationalAlert> alerts = new();
        foreach (Workspace workspace in workspaces)
        {
            tasks.AddRange(await taskRepository.GetForWorkspaceAsync(workspace.Id, cancellationToken));
            notifications.AddRange(await notificationRepository.GetForWorkspaceAsync(workspace.Id, cancellationToken));
            alerts.AddRange(await operationsRepository.GetOpenAlertsAsync(workspace.Id, cancellationToken));
        }

        int openTasks = tasks.Count(item => item.Status != StudioTaskStatus.Completed);
        int pendingNotifications = notifications.Count(item => item.Status == NotificationStatus.Pending);
        int failedNotifications = notifications.Count(item => item.Status == NotificationStatus.Failed);
        return new StudioOperationsSnapshot(DateTimeOffset.UtcNow, workspaces, bookings, customers.Take(200).ToList(), tasks.OrderBy(item => item.Status).ThenBy(item => item.DueUtc).Take(200).ToList(), notifications.OrderByDescending(item => item.CreatedUtc).Take(200).ToList(), alerts.OrderByDescending(item => item.Severity).ThenByDescending(item => item.CreatedUtc).Take(200).ToList(), new StudioOperationsCounts(bookings.Count, customers.Count, openTasks, pendingNotifications, failedNotifications, alerts.Count));
    }
}

public sealed record StudioOperationsSnapshot(DateTimeOffset GeneratedUtc, IReadOnlyList<Workspace> Workspaces, IReadOnlyList<Booking> UpcomingBookings, IReadOnlyList<Customer> Customers, IReadOnlyList<StudioTask> Tasks, IReadOnlyList<NotificationJob> Notifications, IReadOnlyList<OperationalAlert> Alerts, StudioOperationsCounts Counts);
public sealed record StudioOperationsCounts(int UpcomingBookings, int Customers, int OpenTasks, int PendingNotifications, int FailedNotifications, int OpenAlerts);
