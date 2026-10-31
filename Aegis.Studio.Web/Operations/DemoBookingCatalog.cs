using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Operations;

public static class DemoBookingCatalog
{
    public static void Seed(StudioDatabase database)
    {
        var store = new StudioPublicBookingCatalogStore(database);
        store.EnsureSchema();

        store.Save([
            Session("passport-photo", "Passport Photos", "Professional passport photographs with quick studio turnaround.", "ID Services", 20, 250000, 250000, ["BK", "BE"]),
            Session("visa-photo", "Visa Photos", "Visa and immigration photographs prepared to the required dimensions.", "ID Services", 25, 350000, 350000, ["BK", "BE"]),
            Session("business-portrait", "Business Portrait", "A polished individual portrait for business cards, profiles and company websites.", "Portraits", 45, 750000, 250000, ["BK", "BE"]),
            Session("executive-portrait", "Executive Portrait", "Guided executive portrait session with a choice of studio looks.", "Portraits", 60, 1250000, 500000, ["BK", "BE"]),
            Session("family-portrait", "Family Portrait", "A relaxed family portrait session for groups and generations.", "Portraits", 75, 1800000, 600000, ["BK", "BE"]),
            Session("graduation-portrait", "Graduation Portrait", "Graduation portraits for individuals, groups and formal announcements.", "Graduations", 45, 650000, 250000, ["BK", "BE"]),
            Session("special-event-coverage", "Special Event Coverage", "On-location photography for birthdays, corporate events and celebrations.", "Events", 180, 4500000, 1500000, ["BK", "BE"], 2),
            Session("wedding-coverage", "Wedding Coverage", "Wedding photography coverage with preparation, ceremony and reception options.", "Weddings", 480, 12500000, 3000000, ["BK", "BE"], 2),
            Session("photo-restoration-consultation", "Photo Restoration Consultation", "Bring a damaged photograph for assessment and restoration planning.", "Digital Imaging", 30, 500000, 500000, ["BK", "BE"])
        ]);

        var hours = new List<PublicBookingHours>();
        foreach (var location in new[] { "BK", "BE" })
            foreach (var day in new[] { 1, 2, 3, 4, 5, 6 })
                hours.Add(new PublicBookingHours(location, day, "09:00", "17:00", 30));
        store.SaveHours(hours);
    }

    private static PublicBookingSessionType Session(
        string id, string name, string description, string category, int duration, long price, long deposit,
        IReadOnlyList<string> locations, int photographers = 1) =>
        new(id, name, description, duration, 15, 10, price, deposit, "JMD", locations,
            new Dictionary<string, int> { ["Photographer"] = photographers }, true, true, null, category);
}
