using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class AvailabilityService
{
    private readonly IBookingRepository bookingRepository;
    private readonly IStudioCatalogRepository catalogRepository;

    public AvailabilityService(IBookingRepository bookingRepository, IStudioCatalogRepository catalogRepository)
    {
        this.bookingRepository = bookingRepository;
        this.catalogRepository = catalogRepository;
    }

    public async Task<bool> IsAvailableAsync(Guid photographerId, ServiceOffering service, DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        DateTimeOffset endUtc = startUtc.AddMinutes(service.DurationMinutes + service.BufferMinutes);
        IReadOnlyList<Booking> bookings = await bookingRepository.GetForPhotographerAsync(photographerId, startUtc.Date, startUtc.Date.AddDays(1), cancellationToken);
        bool overlaps = bookings.Any(booking => booking.Status != BookingStatus.Cancelled && startUtc < booking.EndUtc.AddMinutes(service.BufferMinutes) && endUtc > booking.StartUtc);
        return !overlaps;
    }
}
