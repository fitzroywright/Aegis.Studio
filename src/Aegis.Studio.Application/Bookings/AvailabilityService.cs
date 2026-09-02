using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class AvailabilityService
{
    private const string StudioTimeZoneId = "America/Jamaica";
    private readonly IBookingRepository bookingRepository;
    private readonly IStudioCatalogRepository catalogRepository;

    public AvailabilityService(IBookingRepository bookingRepository, IStudioCatalogRepository catalogRepository)
    {
        this.bookingRepository = bookingRepository;
        this.catalogRepository = catalogRepository;
    }

    public async Task<bool> IsAvailableAsync(Guid photographerId, ServiceOffering service, DateTimeOffset startUtc, CancellationToken cancellationToken, Guid? excludedBookingId = null)
    {
        if (!await IsWithinWorkingHoursAsync(photographerId, service, startUtc, cancellationToken))
        {
            return false;
        }

        DateTimeOffset endUtc = startUtc.AddMinutes(service.DurationMinutes + service.BufferMinutes);
        IReadOnlyList<Booking> bookings = await bookingRepository.GetForPhotographerAsync(photographerId, startUtc.Date, startUtc.Date.AddDays(1), cancellationToken);
        bool overlaps = bookings.Any(booking => booking.Id != excludedBookingId &&
                                                booking.Status != BookingStatus.Cancelled &&
                                                startUtc < booking.EndUtc.AddMinutes(service.BufferMinutes) &&
                                                endUtc > booking.StartUtc);
        return !overlaps;
    }

    public async Task<IReadOnlyList<Photographer>> GetAvailablePhotographersAsync(ServiceOffering service, DateTimeOffset startUtc, CancellationToken cancellationToken, Guid? excludedBookingId = null)
    {
        IReadOnlyList<Photographer> photographers = await catalogRepository.GetPhotographersAsync(cancellationToken);
        List<Photographer> available = new();
        foreach (Photographer photographer in photographers)
        {
            if (await IsAvailableAsync(photographer.Id, service, startUtc, cancellationToken, excludedBookingId))
            {
                available.Add(photographer);
            }
        }

        return available;
    }

    private async Task<bool> IsWithinWorkingHoursAsync(Guid photographerId, ServiceOffering service, DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(StudioTimeZoneId);
        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(startUtc, timeZone);
        DateTimeOffset localEnd = localStart.AddMinutes(service.DurationMinutes);
        IReadOnlyList<AvailabilityRule> rules = await catalogRepository.GetAvailabilityRulesAsync(photographerId, localStart.DayOfWeek, cancellationToken);
        TimeOnly startTime = TimeOnly.FromDateTime(localStart.DateTime);
        TimeOnly endTime = TimeOnly.FromDateTime(localEnd.DateTime);
        return rules.Any(rule => startTime >= rule.StartLocal && endTime <= rule.EndLocal);
    }
}
