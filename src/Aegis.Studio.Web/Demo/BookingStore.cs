using System.Collections.Concurrent;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Web.Demo;

public sealed class BookingStore
{
    private readonly ConcurrentDictionary<Guid, Booking> bookings = new();
    private readonly ConcurrentDictionary<string, Guid> externalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ExternalPaymentConfirmation> payments = new();

    public IReadOnlyList<Booking> GetAll()
    {
        return bookings.Values.OrderBy(x => x.StartsAt).ToList();
    }

    public Booking? Get(Guid id)
    {
        bookings.TryGetValue(id, out Booking? booking);
        return booking;
    }

    public Booking Create(CreateBookingIntakeRequest request, DemoStore store)
    {
        if (!string.IsNullOrWhiteSpace(request.ExternalBookingId) &&
            externalIds.TryGetValue(request.ExternalBookingId.Trim(), out Guid existingId))
        {
            return bookings[existingId];
        }

        DemoJob job = store.CreateBookingJob(request);
        Booking booking = new()
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            WorkspaceId = job.WorkspaceId,
            ClientName = request.ClientName.Trim(),
            ClientEmail = request.ClientEmail.Trim(),
            ClientPhone = request.ClientPhone?.Trim(),
            SessionType = request.SessionType.Trim(),
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            Location = request.Location?.Trim(),
            Notes = request.Notes?.Trim(),
            Source = ParseSource(request.Source),
            Status = BookingStatus.Requested,
            ExternalBookingId = request.ExternalBookingId?.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        bookings[booking.Id] = booking;
        if (!string.IsNullOrWhiteSpace(booking.ExternalBookingId))
        {
            externalIds.TryAdd(booking.ExternalBookingId, booking.Id);
        }

        return booking;
    }

    public Booking UpdateStatus(Guid id, BookingStatus status)
    {
        Booking booking = bookings[id];
        booking.Status = status;
        return booking;
    }

    public ExternalPaymentConfirmation RecordPayment(Guid bookingId, RecordPaymentConfirmationRequest request)
    {
        ExternalPaymentConfirmation? duplicate = payments.Values.FirstOrDefault(x =>
            string.Equals(x.Reference, request.Reference, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            return duplicate;
        }

        ExternalPaymentConfirmation payment = new()
        {
            Id = Guid.NewGuid(),
            BookingId = bookingId,
            Amount = request.Amount,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            Reference = request.Reference.Trim(),
            Provider = request.Provider.Trim(),
            Source = request.Source?.Trim() ?? "ExternalSite",
            ConfirmedAt = request.ConfirmedAt,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        payments[payment.Id] = payment;
        return payment;
    }

    public IReadOnlyList<ExternalPaymentConfirmation> GetPayments(Guid bookingId)
    {
        return payments.Values.Where(x => x.BookingId == bookingId).OrderBy(x => x.ConfirmedAt).ToList();
    }

    private static BookingSource ParseSource(string? source)
    {
        return Enum.TryParse(source, true, out BookingSource value) ? value : BookingSource.Manual;
    }
}

public sealed record CreateBookingIntakeRequest(
    Guid? WorkspaceId,
    string? Destination,
    string ClientName,
    string ClientEmail,
    string? ClientPhone,
    string SessionType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? Location,
    string? Notes,
    string? Source,
    string? ExternalBookingId);

public sealed record UpdateBookingStatusRequest(BookingStatus Status);

public sealed record RecordPaymentConfirmationRequest(
    decimal Amount,
    string Currency,
    string Reference,
    string Provider,
    string? Source,
    DateTimeOffset ConfirmedAt);
