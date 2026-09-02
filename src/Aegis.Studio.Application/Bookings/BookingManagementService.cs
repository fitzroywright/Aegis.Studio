using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Application.Bookings;

public sealed class BookingManagementService
{
    private readonly IBookingRepository bookingRepository;
    private readonly IStudioCatalogRepository catalogRepository;
    private readonly AvailabilityService availabilityService;
    private readonly BookingLifecycleService bookingLifecycleService;
    private readonly IUnitOfWork unitOfWork;

    public BookingManagementService(IBookingRepository bookingRepository, IStudioCatalogRepository catalogRepository, AvailabilityService availabilityService, BookingLifecycleService bookingLifecycleService, IUnitOfWork unitOfWork)
    {
        this.bookingRepository = bookingRepository;
        this.catalogRepository = catalogRepository;
        this.availabilityService = availabilityService;
        this.bookingLifecycleService = bookingLifecycleService;
        this.unitOfWork = unitOfWork;
    }

    public async Task<Booking> RescheduleAsync(Guid bookingId, RescheduleBookingRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        ServiceOffering service = await catalogRepository.GetServiceAsync(booking.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        Guid photographerId = await ResolvePhotographerAsync(request.PhotographerId ?? booking.PhotographerId, service, request.StartUtc, booking.Id, cancellationToken);

        booking.Reschedule(request.StartUtc, request.StartUtc.AddMinutes(service.DurationMinutes), photographerId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await bookingLifecycleService.BookingRescheduledAsync(booking, cancellationToken);
        return booking;
    }

    public async Task<Booking> CancelAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        booking.Cancel();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await bookingLifecycleService.BookingCancelledAsync(booking, cancellationToken);
        return booking;
    }

    public async Task<Booking> CompleteAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Booking booking = await GetBookingAsync(bookingId, cancellationToken);
        booking.Complete();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await bookingLifecycleService.BookingCompletedAsync(booking, cancellationToken);
        return booking;
    }

    private async Task<Guid> ResolvePhotographerAsync(Guid? requestedPhotographerId, ServiceOffering service, DateTimeOffset startUtc, Guid bookingId, CancellationToken cancellationToken)
    {
        if (requestedPhotographerId is not null)
        {
            bool available = await availabilityService.IsAvailableAsync(requestedPhotographerId.Value, service, startUtc, cancellationToken, bookingId);
            if (!available)
            {
                throw new InvalidOperationException("The selected photographer is not available at that time.");
            }

            return requestedPhotographerId.Value;
        }

        IReadOnlyList<Photographer> availablePhotographers = await availabilityService.GetAvailablePhotographersAsync(service, startUtc, cancellationToken, bookingId);
        Photographer? photographer = availablePhotographers.FirstOrDefault();
        return photographer?.Id ?? throw new InvalidOperationException("No photographer is available at the selected time.");
    }

    private async Task<Booking> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        return await bookingRepository.GetAsync(bookingId, cancellationToken) ?? throw new InvalidOperationException("Booking was not found.");
    }
}
