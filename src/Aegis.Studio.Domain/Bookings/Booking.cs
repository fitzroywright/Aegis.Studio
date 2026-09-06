using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Bookings;

public sealed class Booking : Entity
{
    public Booking(Guid id) : base(id)
    {
    }

    public Guid JobId { get; set; }
    public Guid WorkspaceId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public string ClientEmail { get; set; } = string.Empty;
    public string? ClientPhone { get; set; }
    public string SessionType { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public BookingSource Source { get; set; }
    public BookingStatus Status { get; set; } = BookingStatus.Requested;
    public string? ExternalBookingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
