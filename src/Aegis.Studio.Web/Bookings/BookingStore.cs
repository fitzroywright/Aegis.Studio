using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;

namespace Aegis.Studio.Web.Bookings;

public sealed class BookingStore
{
    private const string BookingsCollection = "bookings";
    private const string PaymentsCollection = "booking-payments";
    private readonly PostgresDocumentStore persistence;

    public BookingStore(PostgresDocumentStore persistence)
    {
        this.persistence = persistence;
    }

    public IReadOnlyList<Booking> GetAll() => persistence.GetAll<Booking>(BookingsCollection).OrderBy(x => x.StartsAt).ToList();
    public Booking? Get(Guid id) => persistence.Get<Booking>(BookingsCollection, id);

    public Booking Create(CreateBookingIntakeRequest request, StudioStore store)
    {
        if (!string.IsNullOrWhiteSpace(request.ExternalBookingId))
        {
            Booking? existing = GetAll().FirstOrDefault(x => string.Equals(x.ExternalBookingId, request.ExternalBookingId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }
        }

        StudioJob job = store.CreateBookingJob(request);
        Booking booking = new(Guid.NewGuid())
        {
            JobId = job.Id,
            WorkspaceId = job.WorkspaceId,
            ClientName = request.ClientName.Trim(),
            ClientEmail = request.ClientEmail.Trim(),
            ClientPhone = request.ClientPhone?.Trim(),
            SessionType = request.SessionType.Trim(),
            StartsAt = request.StartsAt.ToUniversalTime(),
            EndsAt = request.EndsAt.ToUniversalTime(),
            Location = request.Location?.Trim(),
            Notes = request.Notes?.Trim(),
            Source = ParseSource(request.Source),
            Status = BookingStatus.Requested,
            ExternalBookingId = request.ExternalBookingId?.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        persistence.Upsert(BookingsCollection, booking.Id, booking);
        return booking;
    }

    public Booking UpdateStatus(Guid id, BookingStatus status)
    {
        Booking booking = Get(id) ?? throw new KeyNotFoundException("Booking was not found.");
        booking.Status = status;
        persistence.Upsert(BookingsCollection, booking.Id, booking);
        return booking;
    }

    public ExternalPaymentConfirmation RecordPayment(Guid bookingId, RecordPaymentConfirmationRequest request, StudioStore studio)
    {
        ExternalPaymentConfirmation? duplicate = persistence.GetAll<ExternalPaymentConfirmation>(PaymentsCollection)
            .FirstOrDefault(x => string.Equals(x.Reference, request.Reference, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            return duplicate;
        }

        Booking booking = Get(bookingId) ?? throw new KeyNotFoundException("Booking was not found.");
        ExternalPaymentConfirmation payment = new(Guid.NewGuid())
        {
            BookingId = bookingId,
            Amount = request.Amount,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Reference = request.Reference.Trim(),
            Provider = request.Provider.Trim(),
            Source = request.Source?.Trim() ?? "ExternalSite",
            ConfirmedAt = request.ConfirmedAt.ToUniversalTime(),
            ReceivedAt = DateTimeOffset.UtcNow
        };
        persistence.Upsert(PaymentsCollection, payment.Id, payment);
        studio.AddFinancialEvent(booking.JobId, new AddFinancialEventRequest("PaymentConfirmation", payment.Amount, $"{payment.Source} / {payment.Provider} / {payment.Reference}"));
        return payment;
    }

    public IReadOnlyList<ExternalPaymentConfirmation> GetPayments(Guid bookingId)
        => persistence.GetAll<ExternalPaymentConfirmation>(PaymentsCollection).Where(x => x.BookingId == bookingId).OrderBy(x => x.ConfirmedAt).ToList();

    private static BookingSource ParseSource(string? source) => Enum.TryParse(source, true, out BookingSource value) ? value : BookingSource.Manual;
}

public sealed record CreateBookingIntakeRequest(Guid? WorkspaceId, string? Destination, string ClientName, string ClientEmail, string? ClientPhone, string SessionType, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, string? Notes, string? Source, string? ExternalBookingId);
public sealed record UpdateBookingStatusRequest(BookingStatus Status);
public sealed record RecordPaymentConfirmationRequest(decimal Amount, string Currency, string Reference, string Provider, string? Source, DateTimeOffset ConfirmedAt);
