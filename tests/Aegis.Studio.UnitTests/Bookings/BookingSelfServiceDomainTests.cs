using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.UnitTests.Bookings;

public sealed class BookingSelfServiceDomainTests
{
    [Fact]
    public void NewBookingCreatesOpaqueAccessToken()
    {
        Booking booking = CreateBooking();
        Assert.False(string.IsNullOrWhiteSpace(booking.AccessToken));
        Assert.DoesNotContain(booking.Reference, booking.AccessToken, StringComparison.OrdinalIgnoreCase);
        Assert.True(booking.AccessToken.Length >= 32);
    }

    [Fact]
    public void RescheduleChangesTimeAndPhotographer()
    {
        Booking booking = CreateBooking();
        Guid photographerId = Guid.NewGuid();
        DateTimeOffset startUtc = DateTimeOffset.UtcNow.AddDays(5);
        booking.Reschedule(startUtc, startUtc.AddHours(1), photographerId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(photographerId, booking.PhotographerId);
        Assert.Equal(startUtc, booking.StartUtc);
    }

    [Fact]
    public void CompleteChangesStatus()
    {
        Booking booking = CreateBooking();
        booking.Complete();
        Assert.Equal(BookingStatus.Completed, booking.Status);
    }

    private static Booking CreateBooking()
    {
        DateTimeOffset startUtc = DateTimeOffset.UtcNow.AddDays(3);
        return new Booking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test Customer", "customer@example.com", "876-555-0100", startUtc, startUtc.AddHours(1));
    }
}
