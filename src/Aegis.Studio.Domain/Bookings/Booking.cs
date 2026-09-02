namespace Aegis.Studio.Domain.Bookings;

public sealed class Booking
{
    private Booking()
    {
    }

    public Booking(Guid workspaceId, Guid serviceOfferingId, Guid? photographerId, string clientName, string clientEmail, string clientPhone, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        Id = Guid.NewGuid();
        Reference = $"BK-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";
        WorkspaceId = workspaceId;
        ServiceOfferingId = serviceOfferingId;
        PhotographerId = photographerId;
        ClientName = clientName.Trim();
        ClientEmail = clientEmail.Trim();
        ClientPhone = clientPhone.Trim();
        StartUtc = startUtc;
        EndUtc = endUtc;
        Status = BookingStatus.Confirmed;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public Guid WorkspaceId { get; private set; }
    public Guid ServiceOfferingId { get; private set; }
    public Guid? PhotographerId { get; private set; }
    public string ClientName { get; private set; } = string.Empty;
    public string ClientEmail { get; private set; } = string.Empty;
    public string ClientPhone { get; private set; } = string.Empty;
    public DateTimeOffset StartUtc { get; private set; }
    public DateTimeOffset EndUtc { get; private set; }
    public BookingStatus Status { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }

    public void Cancel()
    {
        Status = BookingStatus.Cancelled;
    }

    public void Reschedule(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        StartUtc = startUtc;
        EndUtc = endUtc;
    }
}
