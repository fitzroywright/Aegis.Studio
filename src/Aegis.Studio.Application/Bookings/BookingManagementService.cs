using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class BookingManagementService
{
    private readonly IBookingRepository bookingRepository;
    private readonly IStudioCatalogRepository catalogRepository;
    private readonly AvailabilityService availabilityService;
    private readonly IUnitOfWork unitOfWork;

    public BookingManagementService(IBookingRepository bookingRepository, IStudioCatalogRepository catalogRepository, AvailabilityService availabilityService, IUnitOfWork unitOfWork)
    {
        this.bookingRepository = bookingRepository;
        this.catalogRepository = catalogRepository;
        this.availabilityService = availabilityService;
        this.unitOfWork = unitOfWork;
    }

    public async Task<Booking> RescheduleAsync(Guid bookingId, RescheduleBookingRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        ServiceOffering service = await catalogRepository.GetServiceAsync(booking.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        Guid photographerId = request.PhotographerId ?? booking.PhotographerId ?? throw new InvalidOperationException("A photographer is required.");
        bool available = await availabilityService.IsAvailableAsync(photographerId, service, request.StartUtc, cancellationToken, booking.Id);
        if (!available)
        {
            throw new InvalidOperationException("The selected time is no longer available.");
        }

        booking.Reschedule(request.StartUtc, request.StartUtc.AddMinutes(service.DurationMinutes), photographerId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return booking;
    }

    public async Task<Booking> CancelAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        booking.Cancel();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return booking;
    }

    public async Task<Booking> CompleteAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        booking.Complete();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return booking;
    }

    private async Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        return await bookingRepository.GetAsync(bookingId, cancellationToken) ?? throw new InvalidOperationException("Booking was not found.");
    }
}
