using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Abstractions;

public interface IBookingRepository
{
    Task AddAsync(Booking booking, CancellationToken cancellationToken);
    Task<IReadOnlyList<Booking>> GetForPhotographerAsync(Guid photographerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<Booking>> GetUpcomingAsync(CancellationToken cancellationToken);
}
