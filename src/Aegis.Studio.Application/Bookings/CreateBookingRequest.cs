namespace Aegis.Studio.Application.Bookings;

public sealed record CreateBookingRequest(Guid WorkspaceId, Guid ServiceOfferingId, Guid? PhotographerId, string ClientName, string ClientEmail, string ClientPhone, DateTimeOffset StartUtc);
