using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed record BookingSelfServiceView(string Reference, string ClientName, string ClientEmail, string ServiceName, string? PhotographerName, DateTimeOffset StartUtc, DateTimeOffset EndUtc, BookingStatus Status, string AccessToken);

public sealed record SelfServiceRescheduleRequest(DateTimeOffset StartUtc, Guid? PhotographerId);
