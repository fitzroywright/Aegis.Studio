using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.UnitTests.Bookings;

public sealed class BookingTests
{
    [Fact]
    public void CancelChangesStatus()
    {
        Booking booking = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Jane Brown", "jane@example.com", "555", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1).AddHours(1));
        booking.Cancel();
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }
}
