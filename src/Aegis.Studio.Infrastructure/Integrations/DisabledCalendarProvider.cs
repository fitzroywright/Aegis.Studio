using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Infrastructure.Integrations;

public sealed class DisabledCalendarProvider : ICalendarProvider
{
    public Task<IntegrationResult> UpsertBookingAsync(Booking booking, CancellationToken cancellationToken) => Task.FromResult(new IntegrationResult(false, false, null, "Calendar integration is not configured."));
    public Task<IntegrationResult> DeleteBookingAsync(Booking booking, CancellationToken cancellationToken) => Task.FromResult(new IntegrationResult(false, false, null, "Calendar integration is not configured."));
}
