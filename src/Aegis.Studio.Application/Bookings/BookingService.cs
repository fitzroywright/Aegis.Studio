using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.Application.Bookings;

public sealed class BookingService
{
    private readonly IBookingRepository bookingRepository;
    private readonly ICommunicationRepository communicationRepository;
    private readonly IStudioCatalogRepository catalogRepository;
    private readonly IUnitOfWork unitOfWork;
    private readonly AvailabilityService availabilityService;

    public BookingService(IBookingRepository bookingRepository, ICommunicationRepository communicationRepository, IStudioCatalogRepository catalogRepository, IUnitOfWork unitOfWork, AvailabilityService availabilityService)
    {
        this.bookingRepository = bookingRepository;
        this.communicationRepository = communicationRepository;
        this.catalogRepository = catalogRepository;
        this.unitOfWork = unitOfWork;
        this.availabilityService = availabilityService;
    }

    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken cancellationToken)
    {
        ServiceOffering service = await catalogRepository.GetServiceAsync(request.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        Guid photographerId = request.PhotographerId ?? throw new InvalidOperationException("A photographer is required for the MVP.");
        bool isAvailable = await availabilityService.IsAvailableAsync(photographerId, service, request.StartUtc, cancellationToken);
        if (!isAvailable)
        {
            throw new InvalidOperationException("The selected time is no longer available.");
        }

        Booking booking = new(request.WorkspaceId, request.ServiceOfferingId, photographerId, request.ClientName, request.ClientEmail, request.ClientPhone, request.StartUtc, request.StartUtc.AddMinutes(service.DurationMinutes));
        Conversation conversation = new(request.WorkspaceId, booking.Id, request.ClientEmail, $"Booking {booking.Reference}");
        CommunicationMessage message = new(conversation.Id, MessageDirection.Internal, "system", request.ClientEmail, conversation.Subject, "Booking workspace created.");

        await bookingRepository.AddAsync(booking, cancellationToken);
        await communicationRepository.AddConversationAsync(conversation, cancellationToken);
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return booking;
    }
}
