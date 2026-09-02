using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Abstractions;

public interface ICalendarProvider
{
    Task<IntegrationResult> UpsertBookingAsync(Booking booking, CancellationToken cancellationToken);
    Task<IntegrationResult> DeleteBookingAsync(Booking booking, CancellationToken cancellationToken);
}

public sealed record IntegrationResult(bool Succeeded, bool IsConfigured, string? ExternalId, string? Message);
