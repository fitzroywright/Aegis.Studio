using Aegis.Studio.Domain.Common;

namespace Aegis.Studio.Domain.Bookings;

public sealed class ExternalPaymentConfirmation : Entity
{
    public ExternalPaymentConfirmation(Guid id) : base(id)
    {
    }

    public Guid BookingId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "JMD";
    public string Reference { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset ConfirmedAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
