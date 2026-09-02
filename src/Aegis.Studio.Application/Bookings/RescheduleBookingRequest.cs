namespace Aegis.Studio.Application.Bookings;

public sealed record RescheduleBookingRequest(DateTimeOffset StartUtc, Guid? PhotographerId);
