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
        await SeedCatalogAsync(cancellationToken);
        await SeedDemoCommunicationsAsync(cancellationToken);
        DemoOperationsSeeder operationsSeeder = new(dbContext);
        await operationsSeeder.SeedAsync(cancellationToken);
    }

    private async Task SeedCatalogAsync(CancellationToken cancellationToken)
    {
        await EnsureWorkspaceAsync("Weddings", "weddings", "weddings@bryans.com", cancellationToken);
        await EnsureWorkspaceAsync("Graduation Photos", "graduations", "graduations@bryans.com", cancellationToken);
        await EnsureWorkspaceAsync("Portraits", "portraits", "portraits@bryans.com", cancellationToken);
        await EnsureWorkspaceAsync("ID Photos", "idphotos", "idphotos@bryans.com", cancellationToken);
        await EnsureWorkspaceAsync("Corporate & Events", "corporate", "corporate@bryans.com", cancellationToken);
        Photographer ryan = await EnsurePhotographerAsync("Ryan Bryan", "ryan@bryans.com", cancellationToken);
        Photographer michelle = await EnsurePhotographerAsync("Michelle Grant", "michelle@bryans.com", cancellationToken);
        Photographer andre = await EnsurePhotographerAsync("Andre Campbell", "andre@bryans.com", cancellationToken);
        await EnsureServiceAsync("Wedding Photography", 240, 30, cancellationToken);
        await EnsureServiceAsync("Engagement Session", 90, 15, cancellationToken);
        await EnsureServiceAsync("Portrait Session", 60, 15, cancellationToken);
        await EnsureServiceAsync("Graduation Photography", 90, 15, cancellationToken);
        await EnsureServiceAsync("Corporate Event Coverage", 180, 30, cancellationToken);
        await EnsureServiceAsync("ID & Passport Photos", 30, 5, cancellationToken);
        await EnsureAvailabilityAsync(ryan, cancellationToken);
        await EnsureAvailabilityAsync(michelle, cancellationToken);
        await EnsureAvailabilityAsync(andre, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedDemoCommunicationsAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.Conversations.AnyAsync(cancellationToken)) return;
        List<Workspace> workspaces = await dbContext.Workspaces.ToListAsync(cancellationToken);
        List<ServiceOffering> services = await dbContext.ServiceOfferings.ToListAsync(cancellationToken);
        List<Photographer> photographers = await dbContext.Photographers.ToListAsync(cancellationToken);
        Workspace weddings = FindWorkspace(workspaces, "weddings"); Workspace graduations = FindWorkspace(workspaces, "graduations"); Workspace portraits = FindWorkspace(workspaces, "portraits"); Workspace idPhotos = FindWorkspace(workspaces, "idphotos"); Workspace corporate = FindWorkspace(workspaces, "corporate");
        Photographer ryan = photographers[0]; Photographer michelle = photographers.Count > 1 ? photographers[1] : ryan; Photographer andre = photographers.Count > 2 ? photographers[2] : ryan;
        Booking danielle = AddBooking(weddings, FindService(services, "Wedding Photography"), ryan, "Danielle Morgan", "danielle.morgan@example.com", "876-555-0101", 10, 11);
        Booking kemar = AddBooking(graduations, FindService(services, "Graduation Photography"), michelle, "Kemar Brown", "kemar.brown@example.com", "876-555-0102", 6, 10);
        Booking alana = AddBooking(portraits, FindService(services, "Portrait Session"), andre, "Alana Reid", "alana.reid@example.com", "876-555-0103", 3, 14);
        Booking monique = AddBooking(corporate, FindService(services, "Corporate Event Coverage"), ryan, "Monique Ellis", "monique.ellis@example.com", "876-555-0104", 14, 16);
        Booking jason = AddBooking(idPhotos, FindService(services, "ID & Passport Photos"), michelle, "Jason Clarke", "jason.clarke@example.com", "876-555-0105", 1, 9);
        AddThread(weddings, danielle, "danielle.morgan@example.com", "Wedding photography details", "We are finalizing our wedding timeline and wanted to confirm coverage for getting ready through the reception.", "Absolutely. We have your booking and can review the final timeline with you.");
        AddThread(graduations, kemar, "kemar.brown@example.com", "Graduation package", "Can you confirm what is included in the graduation package and when we should arrive?", "Please arrive 15 minutes early. We will have the package details ready at check-in.");
        AddThread(portraits, alana, "alana.reid@example.com", "Portrait wardrobe question", "Would you recommend bringing a second outfit for my portrait session?", "Yes, a second outfit is a good idea and we can plan the change during your session.");
        AddThread(corporate, monique, "monique.ellis@example.com", "Awards dinner coverage", "We need a photographer for arrivals, awards and executive group photographs.", "Your event is on our schedule. We will coordinate the shot list before the event.");
        AddThread(idPhotos, jason, "jason.clarke@example.com", "Visa photo requirements", "Do you handle Canadian visa photo specifications?", "Yes. Please bring the current specification sheet if you have it and we will prepare the photographs accordingly.");
        AddThread(weddings, null, "shane.williams@example.com", "Wedding availability in November", "Are you available for a November wedding in Kingston?", null);
        AddThread(graduations, null, "natasha.king@example.com", "School graduation portraits", "We are planning graduation portraits for our sixth form class and need package information.", null);
        AddThread(portraits, null, "keisha.thomas@example.com", "Family portrait session", "We would like a family portrait for seven people. What session would you recommend?", "A portrait session is a good fit. We can also discuss print options after the shoot.");
        AddThread(idPhotos, null, "simone.hall@example.com", "Staff ID photos", "Can Bryan Studios photograph approximately 40 staff members for new company IDs?", null);
        AddThread(corporate, null, "marcus.gordon@example.com", "Executive portraits", "We need updated executive portraits for our leadership team.", "We can arrange an on-site or studio session depending on your preference.");
        AddThread(corporate, null, "sophia.bennett@example.com", "Charity gala photography", "Please send information about event coverage for our annual charity gala.", null);
        AddThread(weddings, null, "nadine.foster@example.com", "Engagement photographs", "We would like an engagement session before our wedding next year.", "We would be happy to help. Please let us know your preferred month and location.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Workspace> EnsureWorkspaceAsync(string name, string slug, string email, CancellationToken cancellationToken) { Workspace? item = await dbContext.Workspaces.SingleOrDefaultAsync(x => x.Slug == slug, cancellationToken); if (item is not null) return item; item = new Workspace(name, slug, email); dbContext.Workspaces.Add(item); return item; }
    private async Task<Photographer> EnsurePhotographerAsync(string name, string email, CancellationToken cancellationToken) { Photographer? item = await dbContext.Photographers.SingleOrDefaultAsync(x => x.NotificationEmail == email, cancellationToken); if (item is not null) return item; item = new Photographer(name, email); dbContext.Photographers.Add(item); return item; }
    private async Task<ServiceOffering> EnsureServiceAsync(string name, int duration, int buffer, CancellationToken cancellationToken) { ServiceOffering? item = await dbContext.ServiceOfferings.SingleOrDefaultAsync(x => x.Name == name, cancellationToken); if (item is not null) return item; item = new ServiceOffering(name, duration, buffer); dbContext.ServiceOfferings.Add(item); return item; }
    private async Task EnsureAvailabilityAsync(Photographer photographer, CancellationToken cancellationToken) { if (await dbContext.AvailabilityRules.AnyAsync(x => x.PhotographerId == photographer.Id, cancellationToken)) return; foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday }) dbContext.AvailabilityRules.Add(new AvailabilityRule(photographer.Id, day, new TimeOnly(9, 0), new TimeOnly(18, 0))); }
    private Booking AddBooking(Workspace workspace, ServiceOffering service, Photographer photographer, string name, string email, string phone, int days, int hour) { DateTimeOffset start = DateTimeOffset.UtcNow.Date.AddDays(days).AddHours(hour); Booking booking = new(workspace.Id, service.Id, photographer.Id, name, email, phone, start, start.AddMinutes(service.DurationMinutes)); dbContext.Bookings.Add(booking); return booking; }
    private void AddThread(Workspace workspace, Booking? booking, string email, string subject, string inbound, string? outbound) { Conversation conversation = new(workspace.Id, booking?.Id, email, subject); dbContext.Conversations.Add(conversation); CommunicationMessage first = CommunicationMessage.Inbound(conversation.Id, email, workspace.PublicEmailAddress, subject, inbound, $"demo-{Guid.NewGuid():N}@example.com"); dbContext.CommunicationMessages.Add(first); conversation.RecordInbound(first.CreatedUtc); if (outbound is not null) { CommunicationMessage reply = CommunicationMessage.Outbound(conversation.Id, workspace.PublicEmailAddress, email, subject, outbound); dbContext.CommunicationMessages.Add(reply); conversation.RecordStaffResponse(reply.CreatedUtc); } }
    private static Workspace FindWorkspace(IEnumerable<Workspace> items, string slug) => items.Single(x => x.Slug == slug);
    private static ServiceOffering FindService(IEnumerable<ServiceOffering> items, string name) => items.Single(x => x.Name == name);
}
