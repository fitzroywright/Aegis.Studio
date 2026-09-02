using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Studio.Infrastructure.Persistence;

public sealed class DatabaseInitializer
{
    private readonly StudioDbContext dbContext;

    public DatabaseInitializer(StudioDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        if (await dbContext.Workspaces.AnyAsync(cancellationToken))
        {
            return;
        }

        Workspace weddings = new("Weddings", "weddings", "weddings@bryans.com");
        Workspace graduations = new("Graduation Photos", "graduations", "graduations@bryans.com");
        Photographer primary = new("Primary Photographer", "manager@bryans.com");
        ServiceOffering wedding = new("Wedding Photography", 240, 30);
        ServiceOffering portrait = new("Portrait Session", 60, 15);

        dbContext.Workspaces.AddRange(weddings, graduations);
        dbContext.Photographers.Add(primary);
        dbContext.ServiceOfferings.AddRange(wedding, portrait);
        foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday })
        {
            dbContext.AvailabilityRules.Add(new AvailabilityRule(primary.Id, day, new TimeOnly(9, 0), new TimeOnly(18, 0)));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
