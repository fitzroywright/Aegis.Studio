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
    }

    private async Task SeedCatalogAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.Workspaces.AnyAsync(cancellationToken))
        {
            return;
        }

        Workspace weddings = new("Weddings", "weddings", "weddings@bryans.com");
        Workspace graduations = new("Graduation Photos", "graduations", "graduations@bryans.com");
        Workspace portraits = new("Portraits", "portraits", "portraits@bryans.com");
        Workspace idPhotos = new("ID Photos", "idphotos", "idphotos@bryans.com");
        Workspace corporate = new("Corporate & Events", "corporate", "corporate@bryans.com");

        Photographer ryan = new("Ryan Bryan", "ryan@bryans.com");
        Photographer michelle = new("Michelle Grant", "michelle@bryans.com");
        Photographer andre = new("Andre Campbell", "andre@bryans.com");

        ServiceOffering wedding = new("Wedding Photography", 240, 30);
        ServiceOffering engagement = new("Engagement Session", 90, 15);
        ServiceOffering portrait = new("Portrait Session", 60, 15);
        ServiceOffering graduation = new("Graduation Photography", 90, 15);
        ServiceOffering corporateEvent = new("Corporate Event Coverage", 180, 30);
        ServiceOffering idPhoto = new("ID & Passport Photos", 30, 5);

        dbContext.Workspaces.AddRange(weddings, graduations, portraits, idPhotos, corporate);
        dbContext.Photographers.AddRange(ryan, michelle, andre);
        dbContext.ServiceOfferings.AddRange(wedding, engagement, portrait, graduation, corporateEvent, idPhoto);

        foreach (Photographer photographer in new[] { ryan, michelle, andre })
        {
            foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday })
            {
                dbContext.AvailabilityRules.Add(new AvailabilityRule(photographer.Id, day, new TimeOnly(9, 0), new TimeOnly(18, 0)));
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedDemoCommunicationsAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.Conversations.AnyAsync(cancellationToken))
        {
            return;
        }

        List<Workspace> workspaces = await dbContext.Workspaces.ToListAsync(cancellationToken);
        List<Photographer> photographers = await dbContext.Photographers.ToListAsync(cancellationToken);
        List<ServiceOffering> services = await dbContext.ServiceOfferings.ToListAsync(cancellationToken);

        Workspace weddings = FindWorkspace(workspaces, "weddings");
        Workspace graduations = FindWorkspace(workspaces, "graduations");
        Workspace portraits = FindWorkspace(workspaces, "portraits");
        Workspace idPhotos = FindWorkspace(workspaces, "idphotos");
        Workspace corporate = FindWorkspace(workspaces, "corporate");
        Photographer primary = photographers.First();

        ServiceOffering weddingService = FindService(services, "Wedding Photography");
        ServiceOffering graduationService = FindService(services, "Graduation Photography");
        ServiceOffering portraitService = FindService(services, "Portrait Session");
        ServiceOffering corporateService = FindService(services, "Corporate Event Coverage");
        ServiceOffering idService = FindService(services, "ID & Passport Photos");

        Booking weddingBooking = AddBooking(weddings, weddingService, primary, "Danielle Morgan", "danielle.morgan@example.com", "876-555-0101", 18, 13, 4);
        Booking graduationBooking = AddBooking(graduations, graduationService, primary, "Kemar Brown", "kemar.brown@example.com", "876-555-0102", 8, 10, 2);
        Booking portraitBooking = AddBooking(portraits, portraitService, primary, "Alana Reid", "alana.reid@example.com", "876-555-0103", 5, 15, 1);
        Booking corporateBooking = AddBooking(corporate, corporateService, primary, "Monique Ellis", "monique.ellis@example.com", "876-555-0104", 12, 9, 3);
        Booking idBooking = AddBooking(idPhotos, idService, primary, "Jason Clarke", "jason.clarke@example.com", "876-555-0105", 2, 11, 1);

        AddThread(weddings, weddingBooking, "danielle.morgan@example.com", "Wedding coverage for November", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "danielle.morgan@example.com", "Hi Bryan Studios, we are getting married in November at Hope Gardens and would love full-day photography. Could you confirm what is included and whether an engagement session can be added?"),
            (MessageDirection.Outbound, weddings.PublicEmailAddress, "Hi Danielle, congratulations! Yes, we can cover preparations through the reception and can add an engagement session. We have linked your inquiry to your booking so the Weddings team can keep everything together."),
            (MessageDirection.Inbound, "danielle.morgan@example.com", "Wonderful, thank you. We would also like a second photographer for the ceremony and family portraits. Can you send the options?"));

        AddThread(weddings, null, "shane.williams@example.com", "Availability for December 19 wedding", ConversationStatus.New, true,
            (MessageDirection.Inbound, "shane.williams@example.com", "Good afternoon. Are you available for a wedding on December 19? Ceremony is in Kingston and reception is in St. Andrew. Approximately 140 guests."));

        AddThread(weddings, null, "natasha.king@example.com", "Wedding album upgrade", ConversationStatus.WaitingForCustomer, false,
            (MessageDirection.Inbound, "natasha.king@example.com", "Can we upgrade our package to include two parent albums?"),
            (MessageDirection.Outbound, weddings.PublicEmailAddress, "Absolutely. We can add two parent albums. Please confirm whether you prefer the 8x8 or 10x10 format and we will update the package."));

        AddThread(weddings, null, "keisha.thomas@example.com", "Venue walkthrough and shot list", ConversationStatus.Resolved, false,
            (MessageDirection.Inbound, "keisha.thomas@example.com", "Do you need to visit the venue before our wedding? I also have a list of family groupings."),
            (MessageDirection.Outbound, weddings.PublicEmailAddress, "A walkthrough is helpful but not mandatory. Please send the family grouping list here and we will attach it to the wedding workspace."),
            (MessageDirection.Inbound, "keisha.thomas@example.com", "Sent. Thank you!"),
            (MessageDirection.Outbound, weddings.PublicEmailAddress, "Received and added to your file. You are all set."));

        AddThread(graduations, graduationBooking, "kemar.brown@example.com", "UTECH graduation package question", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "kemar.brown@example.com", "I booked the graduation session. Can my parents join for two family photographs and can I bring a second outfit?"),
            (MessageDirection.Outbound, graduations.PublicEmailAddress, "Yes to both. Family photographs are welcome, and you may bring a second outfit. We recommend arriving 10 minutes early so the session stays on schedule."),
            (MessageDirection.Inbound, "kemar.brown@example.com", "Perfect. There will be four family members with me."));

        AddThread(graduations, null, "registrar@example.edu", "Group photography for graduation class", ConversationStatus.New, true,
            (MessageDirection.Inbound, "registrar@example.edu", "We are coordinating graduation photography for approximately 220 students. Please advise on group-photo logistics, ordering and delivery timelines."));

        AddThread(graduations, null, "simone.hall@example.com", "When will graduation photos be ready?", ConversationStatus.WaitingForCustomer, false,
            (MessageDirection.Inbound, "simone.hall@example.com", "Hi, I photographed last Saturday. When should I expect the gallery?"),
            (MessageDirection.Outbound, graduations.PublicEmailAddress, "Your gallery is scheduled for delivery within 7-10 business days. We will email you as soon as it is published. Could you confirm the email address used at the session?"));

        AddThread(portraits, portraitBooking, "alana.reid@example.com", "Move family portrait to Saturday", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "alana.reid@example.com", "Could we move our family portrait from Friday afternoon to Saturday morning? One of the children now has school activities."),
            (MessageDirection.Outbound, portraits.PublicEmailAddress, "We can help with that. Saturday at 10:30 AM is currently available. Reply YES and we will move the booking."),
            (MessageDirection.Inbound, "alana.reid@example.com", "YES please. 10:30 works for everyone."));

        AddThread(portraits, null, "marcus.gordon@example.com", "Professional headshots for LinkedIn", ConversationStatus.New, true,
            (MessageDirection.Inbound, "marcus.gordon@example.com", "I need updated professional headshots for LinkedIn and my company profile. Do you offer multiple backgrounds and same-week delivery?"));

        AddThread(portraits, null, "sophia.bennett@example.com", "Maternity portrait ideas", ConversationStatus.Open, false,
            (MessageDirection.Inbound, "sophia.bennett@example.com", "I am interested in a maternity session and would like something elegant and simple. Can you recommend the best time of day?"),
            (MessageDirection.Internal, "Michelle Grant", "Client likes a clean editorial look. Suggest studio plus one outdoor setup near golden hour."),
            (MessageDirection.Outbound, portraits.PublicEmailAddress, "We would love to create that with you. For a soft outdoor look, late afternoon is ideal. We can also combine a studio setup with a short outdoor portion."));

        AddThread(idPhotos, idBooking, "jason.clarke@example.com", "Canadian visa photo dimensions", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "jason.clarke@example.com", "I have an appointment for Canadian visa photos. Do you already have the required dimensions and can I get digital copies as well?"),
            (MessageDirection.Outbound, idPhotos.PublicEmailAddress, "Yes. We will prepare the photographs to the required specification and can provide both printed and digital copies."));

        AddThread(idPhotos, null, "hr@examplecompany.com", "Staff ID photos for 45 employees", ConversationStatus.New, true,
            (MessageDirection.Inbound, "hr@examplecompany.com", "We need staff ID photographs for approximately 45 employees. Can Bryan Studios come to our office and deliver named digital files for our card system?"));

        AddThread(idPhotos, null, "nadine.foster@example.com", "Passport photos ready for pickup", ConversationStatus.Resolved, false,
            (MessageDirection.Inbound, "nadine.foster@example.com", "Are my passport photos ready? I came in yesterday afternoon."),
            (MessageDirection.Outbound, idPhotos.PublicEmailAddress, "Yes, Nadine. Your photographs are ready for collection at the studio."),
            (MessageDirection.Inbound, "nadine.foster@example.com", "Great, I will collect them after lunch."));

        AddThread(corporate, corporateBooking, "monique.ellis@example.com", "Annual awards dinner coverage", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "monique.ellis@example.com", "For our awards dinner we need arrivals, stage presentations, table photographs and a small portrait area. Can you provide two photographers from 5:30 PM to 10:00 PM?"),
            (MessageDirection.Internal, "Ryan Bryan", "Priority corporate client. Hold two photographers pending final run-of-show."),
            (MessageDirection.Outbound, corporate.PublicEmailAddress, "Yes, we can structure the coverage with two photographers and a portrait station. Please send the event run-of-show and venue contact so we can finalize logistics."),
            (MessageDirection.Inbound, "monique.ellis@example.com", "Run-of-show attached. Venue is AC Hotel Kingston and the event manager is Carla."));

        AddThread(corporate, null, "marketing@examplebank.com", "Executive portraits - 18 people", ConversationStatus.New, true,
            (MessageDirection.Inbound, "marketing@examplebank.com", "We are refreshing our leadership page and need consistent executive portraits for 18 people. We would prefer the session at our New Kingston office."));

        AddThread(corporate, null, "events@examplefoundation.org", "Charity gala photography quotation", ConversationStatus.WaitingForCustomer, false,
            (MessageDirection.Inbound, "events@examplefoundation.org", "Please quote photography for our annual charity gala, 6 PM to 11 PM, approximately 300 attendees."),
            (MessageDirection.Outbound, corporate.PublicEmailAddress, "We can prepare the quotation. Please confirm the event date, venue, whether you require instant social-media selects, and whether there will be a step-and-repeat area."));

        AddThread(weddings, null, "olivia.james@example.com", "Thank you for the wedding photos", ConversationStatus.Resolved, false,
            (MessageDirection.Inbound, "olivia.james@example.com", "We received the gallery and absolutely love the photographs. Thank you to the entire team for capturing the day so beautifully."),
            (MessageDirection.Outbound, weddings.PublicEmailAddress, "Thank you, Olivia. It was a pleasure photographing your wedding. We are delighted that you love the images and will pass your message to the team."));

        AddThread(graduations, null, "dwayne.scott@example.com", "Extra graduation prints", ConversationStatus.Open, true,
            (MessageDirection.Inbound, "dwayne.scott@example.com", "My grandmother would like additional 8x10 prints from my graduation session. How do I order three more?"));

        AddThread(portraits, null, "kimberly.lee@example.com", "Birthday portrait session", ConversationStatus.New, true,
            (MessageDirection.Inbound, "kimberly.lee@example.com", "I would like to book a birthday portrait session for my mother. We want something classic, with hair and makeup completed before we arrive. What should we bring?"));

        AddThread(corporate, null, "admin@examplelaw.com", "Firm group photo and partner headshots", ConversationStatus.Open, false,
            (MessageDirection.Inbound, "admin@examplelaw.com", "We need one full-firm group photograph and individual headshots for nine partners. Is it possible to complete everything in one morning?"),
            (MessageDirection.Outbound, corporate.PublicEmailAddress, "Yes. With a prepared room and a firm arrival schedule, one morning is practical. We recommend photographing the full group first and then moving into individual portraits."));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Booking AddBooking(Workspace workspace, ServiceOffering service, Photographer photographer, string name, string email, string phone, int daysFromNow, int hour, int durationHours)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.Date.AddDays(daysFromNow).AddHours(hour);
        Booking booking = new(workspace.Id, service.Id, photographer.Id, name, email, phone, start, start.AddHours(durationHours));
        dbContext.Bookings.Add(booking);
        return booking;
    }

    private void AddThread(Workspace workspace, Booking? booking, string clientEmail, string subject, ConversationStatus status, bool unread, params (MessageDirection Direction, string From, string Body)[] messages)
    {
        Conversation conversation = new(workspace.Id, booking?.Id, clientEmail, subject);
        foreach ((MessageDirection direction, string from, string body) in messages)
        {
            string to = direction == MessageDirection.Inbound ? workspace.PublicEmailAddress : clientEmail;
            CommunicationMessage message = new(conversation.Id, direction, from, to, subject, body, $"<demo-{Guid.NewGuid():N}@bryans.local>");
            dbContext.CommunicationMessages.Add(message);
            if (direction == MessageDirection.Inbound)
            {
                conversation.ReceiveInbound();
            }
            else if (direction == MessageDirection.Outbound)
            {
                conversation.MarkWaitingForCustomer();
            }
            else
            {
                conversation.Touch();
            }
        }

        if (!unread)
        {
            conversation.MarkRead();
        }

        SetStatus(conversation, status);
        dbContext.Conversations.Add(conversation);
    }

    private static void SetStatus(Conversation conversation, ConversationStatus status)
    {
        switch (status)
        {
            case ConversationStatus.New:
                break;
            case ConversationStatus.Open:
                conversation.Reopen();
                break;
            case ConversationStatus.WaitingForCustomer:
                conversation.MarkWaitingForCustomer();
                break;
            case ConversationStatus.Resolved:
                conversation.Resolve();
                break;
            case ConversationStatus.Closed:
                conversation.Close();
                break;
        }
    }

    private static Workspace FindWorkspace(IEnumerable<Workspace> workspaces, string slug) => workspaces.Single(item => item.Slug == slug);
    private static ServiceOffering FindService(IEnumerable<ServiceOffering> services, string name) => services.Single(item => item.Name == name);
}
