using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Customers;
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
    private readonly CustomerService customerService;
    private readonly BookingLifecycleService bookingLifecycleService;

    public BookingService(IBookingRepository bookingRepository, ICommunicationRepository communicationRepository, IStudioCatalogRepository catalogRepository, IUnitOfWork unitOfWork, AvailabilityService availabilityService, CustomerService customerService, BookingLifecycleService bookingLifecycleService)
    {
        this.bookingRepository = bookingRepository;
        this.communicationRepository = communicationRepository;
        this.catalogRepository = catalogRepository;
        this.unitOfWork = unitOfWork;
        this.availabilityService = availabilityService;
        this.customerService = customerService;
        this.bookingLifecycleService = bookingLifecycleService;
    }

    public async Task<Booking> CreateAsync(CreateBookingRequest request, CancellationToken cancellationToken)
    {
        ServiceOffering service = await catalogRepository.GetServiceAsync(request.ServiceOfferingId, cancellationToken) ?? throw new InvalidOperationException("Service was not found.");
        Guid photographerId = await ResolvePhotographerAsync(request.PhotographerId, service, request.StartUtc, cancellationToken);

        await customerService.UpsertAsync(request.ClientName, request.ClientEmail, request.ClientPhone, cancellationToken);

        Booking booking = new(request.WorkspaceId, request.ServiceOfferingId, photographerId, request.ClientName, request.ClientEmail, request.ClientPhone, request.StartUtc, request.StartUtc.AddMinutes(service.DurationMinutes));
        Conversation conversation = new(request.WorkspaceId, booking.Id, request.ClientEmail, $"Booking {booking.Reference}");
        CommunicationMessage message = new(conversation.Id, MessageDirection.Internal, "system", request.ClientEmail, conversation.Subject, "Booking workspace created.");

        await bookingRepository.AddAsync(booking, cancellationToken);
        await communicationRepository.AddConversationAsync(conversation, cancellationToken);
        await communicationRepository.AddMessageAsync(message, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await bookingLifecycleService.BookingCreatedAsync(booking, cancellationToken);
        return booking;
    }

    private async Task<Guid> ResolvePhotographerAsync(Guid? requestedPhotographerId, ServiceOffering service, DateTimeOffset startUtc, CancellationToken cancellationToken)
    {
        if (requestedPhotographerId is not null)
        {
            bool available = await availabilityService.IsAvailableAsync(requestedPhotographerId.Value, service, startUtc, cancellationToken);
            if (!available)
            {
                throw new InvalidOperationException("The selected photographer is not available at that time.");
            }

            return requestedPhotographerId.Value;
        }

        IReadOnlyList<Photographer> availablePhotographers = await availabilityService.GetAvailablePhotographersAsync(service, startUtc, cancellationToken);
        Photographer? photographer = availablePhotographers.FirstOrDefault();
        return photographer?.Id ?? throw new InvalidOperationException("No photographer is available at the selected time.");
    }
}
