using Aegis.Studio.Domain.Customers;
using Aegis.Studio.Domain.Notifications;
using Aegis.Studio.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class DemoOperationsSeeder
{
    private readonly StudioDbContext dbContext;

    public DemoOperationsSeeder(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await SeedCustomersAsync(cancellationToken);
        await SeedTasksAsync(cancellationToken);
        await SeedNotificationsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedCustomersAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.Customers.AnyAsync(cancellationToken))
        {
            return;
        }

        List<(string Name, string Email, string Phone)> customers = new()
        {
            ("Danielle Morgan", "danielle.morgan@example.com", "876-555-0101"),
            ("Kemar Brown", "kemar.brown@example.com", "876-555-0102"),
            ("Alana Reid", "alana.reid@example.com", "876-555-0103"),
            ("Monique Ellis", "monique.ellis@example.com", "876-555-0104"),
            ("Jason Clarke", "jason.clarke@example.com", "876-555-0105"),
            ("Shane Williams", "shane.williams@example.com", "876-555-0110"),
            ("Natasha King", "natasha.king@example.com", "876-555-0111"),
            ("Keisha Thomas", "keisha.thomas@example.com", "876-555-0112"),
            ("Simone Hall", "simone.hall@example.com", "876-555-0113"),
            ("Marcus Gordon", "marcus.gordon@example.com", "876-555-0114"),
            ("Sophia Bennett", "sophia.bennett@example.com", "876-555-0115"),
            ("Nadine Foster", "nadine.foster@example.com", "876-555-0116")
        };

        foreach ((string name, string email, string phone) in customers)
        {
            dbContext.Customers.Add(new Customer(name, email, phone));
        }
    }

    private async Task SeedTasksAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.StudioTasks.AnyAsync(cancellationToken))
        {
            return;
        }

        List<Aegis.Studio.Domain.Communications.Workspace> workspaces = await dbContext.Workspaces.OrderBy(item => item.Name).ToListAsync(cancellationToken);
        foreach (Aegis.Studio.Domain.Communications.Workspace workspace in workspaces)
        {
            dbContext.StudioTasks.Add(new StudioTask(workspace.Id, null, null, "Review today's Workspace", "Review new bookings, open conversations and anything requiring assignment.", null, DateTimeOffset.UtcNow.AddHours(1)));
            dbContext.StudioTasks.Add(new StudioTask(workspace.Id, null, null, "Confirm tomorrow's schedule", "Confirm photographer assignments and customer readiness for tomorrow's sessions.", null, DateTimeOffset.UtcNow.AddHours(6)));
        }
    }

    private async Task SeedNotificationsAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.NotificationJobs.AnyAsync(cancellationToken))
        {
            return;
        }

        List<Aegis.Studio.Domain.Bookings.Booking> bookings = await dbContext.Bookings.OrderBy(item => item.StartUtc).Take(5).ToListAsync(cancellationToken);
        foreach (Aegis.Studio.Domain.Bookings.Booking booking in bookings)
        {
            dbContext.NotificationJobs.Add(new NotificationJob(booking.WorkspaceId, booking.Id, "Email", booking.ClientEmail, $"Upcoming session - {booking.Reference}", $"Reminder for Bryan Studios booking {booking.Reference}.", DateTimeOffset.UtcNow.AddHours(4)));
        }
    }
}
