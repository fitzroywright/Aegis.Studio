using Aegis.Studio.Domain.Bookings;
using Xunit;

namespace Aegis.Studio.UnitTests.Bookings;

public sealed class BookingManagementTests
{
    [Fact]
    public void RescheduleChangesTimeAndPhotographer()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(2);
        Booking booking = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Jane", "jane@example.com", "", start, start.AddHours(1));
        Guid photographerId = Guid.NewGuid();
        DateTimeOffset newStart = start.AddHours(2);
        booking.Reschedule(newStart, newStart.AddHours(1), photographerId);
        Assert.Equal(newStart, booking.StartUtc);
        Assert.Equal(photographerId, booking.PhotographerId);
    }

    [Fact]
    public void CompleteChangesStatus()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow.AddDays(1);
        Booking booking = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Jane", "jane@example.com", "", start, start.AddHours(1));
        booking.Complete();
        Assert.Equal(BookingStatus.Completed, booking.Status);
    }
}
