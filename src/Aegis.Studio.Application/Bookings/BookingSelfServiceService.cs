using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class BookingSelfServiceService
{
    private readonly IBookingRepository bookingRepository;
    private readonly IStudioCatalogRepository catalogRepository;
    private readonly AvailabilityService availabilityService;
    private readonly BookingManagementService bookingManagementService;

    public BookingSelfServiceService(IBookingRepository bookingRepository, IStudioCatalogRepository catalogRepository, AvailabilityService availabilityService, BookingManagementService bookingManagementService)
    {
        this.bookingRepository = bookingRepository;
        this.catalogRepository = catalogRepository;
        this.availabilityService = availabilityService;
        this.bookingManagementService = bookingManagementService;
    }

    public async Task<BookingSelfServiceView> GetAsync(string accessToken, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(accessToken, cancellationToken);
        ServiceOffering service = await catalogRepository.GetServiceAsync(booking.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        Photographer? photographer = booking.PhotographerId is null ? null : await catalogRepository.GetPhotographerAsync(booking.PhotographerId.Value, cancellationToken);
        return new BookingSelfServiceView(booking.Reference, booking.ClientName, booking.ClientEmail, service.Name, photographer?.Name, booking.StartUtc, booking.EndUtc, booking.Status, booking.AccessToken);
    }

    public async Task<IReadOnlyList<Photographer>> GetAvailabilityAsync(string accessToken, DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(accessToken, cancellationToken);
        EnsureCanChange(booking);
        ServiceOffering service = await catalogRepository.GetServiceAsync(booking.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        return await availabilityService.GetAvailablePhotographersAsync(service, startUtc, cancellationToken, booking.Id);
    }

    public async Task<BookingSelfServiceView> RescheduleAsync(string accessToken, SelfServiceRescheduleRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(accessToken, cancellationToken);
        EnsureCanChange(booking);
        await bookingManagementService.RescheduleAsync(booking.Id, new RescheduleBookingRequest(request.StartUtc, request.PhotographerId), cancellationToken);
        return await GetAsync(accessToken, cancellationToken);
    }

    public async Task<BookingSelfServiceView> CancelAsync(string accessToken, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(accessToken, cancellationToken);
        EnsureCanChange(booking);
        await bookingManagementService.CancelAsync(booking.Id, cancellationToken);
        return await GetAsync(accessToken, cancellationToken);
    }

    private async Task<Booking> GetBookingAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Booking access token is required.");
        }

        return await bookingRepository.FindByAccessTokenAsync(accessToken.Trim(), cancellationToken) ?? throw new InvalidOperationException("Booking was not found.");
    }

    private static void EnsureCanChange(Booking booking)
    {
        if (booking.Status is BookingStatus.Cancelled or BookingStatus.Completed)
        {
            throw new InvalidOperationException("This booking can no longer be changed online.");
        }

        if (booking.StartUtc <= DateTimeOffset.UtcNow.AddHours(2))
        {
            throw new InvalidOperationException("Online changes close two hours before the session. Please contact Bryan Studios.");
        }
    }
}
