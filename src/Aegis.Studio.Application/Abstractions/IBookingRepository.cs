using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Abstractions;

public interface IBookingRepository
{
    Task AddAsync(Booking booking, CancellationToken cancellationToken);
    Task<Booking?> GetAsync(Guid bookingId, CancellationToken cancellationToken);
    Task<Booking?> FindByReferenceAsync(string reference, CancellationToken cancellationToken);
    Task<Booking?> FindByAccessTokenAsync(string accessToken, CancellationToken cancellationToken);
    Task<Booking?> FindActiveByClientAsync(Guid workspaceId, string clientEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<Booking>> GetForPhotographerAsync(Guid photographerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<Booking>> GetForWorkspaceAsync(Guid workspaceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<Booking>> GetUpcomingAsync(CancellationToken cancellationToken);
}
