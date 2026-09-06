using System.Collections.Concurrent;
using Aegis.Studio.Domain.Bookings;

namespace Aegis.Studio.Web.Demo;

public sealed class BookingStore
{
    private readonly ConcurrentDictionary<Guid, Booking> bookings = new();
    private readonly ConcurrentDictionary<string, Guid> externalIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ExternalPaymentConfirmation> payments = new();

    public BookingStore(DemoStore studio)
    {
        Seed(studio);
    }

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
        if (!string.IsNullOrWhiteSpace(request.ExternalBookingId) && externalIds.TryGetValue(request.ExternalBookingId.Trim(), out Guid existingId))
        {
            return bookings[existingId];
        }

        DemoJob job = store.CreateBookingJob(request);
        Booking booking = new(Guid.NewGuid())
        {
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

    public ExternalPaymentConfirmation RecordPayment(Guid bookingId, RecordPaymentConfirmationRequest request, DemoStore studio)
    {
        ExternalPaymentConfirmation? duplicate = payments.Values.FirstOrDefault(x => string.Equals(x.Reference, request.Reference, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            return duplicate;
        }

        ExternalPaymentConfirmation payment = new(Guid.NewGuid())
        {
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

        Booking booking = bookings[bookingId];
        studio.AddFinancialEvent(
            booking.JobId,
            new AddFinancialEventRequest(
                "PaymentConfirmation",
                payment.Amount,
                $"{payment.Source} / {payment.Provider} / {payment.Reference}"));

        return payment;
    }

    public IReadOnlyList<ExternalPaymentConfirmation> GetPayments(Guid bookingId)
    {
        return payments.Values.Where(x => x.BookingId == bookingId).OrderBy(x => x.ConfirmedAt).ToList();
    }

    private void Seed(DemoStore studio)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Booking onlineWedding = Create(
            new CreateBookingIntakeRequest(
                null,
                "weddings@bryanstudiosltd.com",
                "Alicia Campbell",
                "alicia.campbell@example.com",
                "+1-876-555-0101",
                "Wedding Photography",
                now.AddDays(18).Date.AddHours(15),
                now.AddDays(18).Date.AddHours(21),
                "Hope Gardens",
                "Public website booking. Couple selected the Signature Wedding package.",
                "Online",
                "WEB-2026-0906-001"),
            studio);
        UpdateStatus(onlineWedding.Id, BookingStatus.Confirmed);
        RecordPayment(
            onlineWedding.Id,
            new RecordPaymentConfirmationRequest(
                25000m,
                "JMD",
                "WEBPAY-0906-001",
                "Website Checkout",
                "ExternalSite",
                now.AddMinutes(-35)),
            studio);

        Booking walkIn = Create(
            new CreateBookingIntakeRequest(
                null,
                "idphotos@bryanstudiosltd.com",
                "Damian Lewis",
                "damian.lewis@example.com",
                "+1-876-555-0102",
                "Passport and ID Photos",
                now.AddHours(2),
                now.AddHours(2).AddMinutes(30),
                "Bryan's Studio - Front Desk",
                "Walk-in customer. Front desk created the booking while the customer was present.",
                "WalkIn",
                null),
            studio);
        UpdateStatus(walkIn.Id, BookingStatus.Confirmed);
        RecordPayment(
            walkIn.Id,
            new RecordPaymentConfirmationRequest(
                3500m,
                "JMD",
                "POS-0906-0142",
                "Front Desk POS",
                "InStore",
                now.AddMinutes(-12)),
            studio);

        Booking phone = Create(
            new CreateBookingIntakeRequest(
                null,
                "graduations@bryanstudiosltd.com",
                "Kendra Morgan",
                "kendra.morgan@example.com",
                "+1-876-555-0103",
                "Family Portrait Session",
                now.AddDays(4).Date.AddHours(11),
                now.AddDays(4).Date.AddHours(12),
                "Bryan's Studio",
                "Customer called to ask for a Saturday family portrait. Waiting for final time confirmation.",
                "Phone",
                null),
            studio);
        UpdateStatus(phone.Id, BookingStatus.PendingConfirmation);

        Booking whatsapp = Create(
            new CreateBookingIntakeRequest(
                null,
                "graduations@bryanstudiosltd.com",
                "Andre Reid",
                "andre.reid@example.com",
                "+1-876-555-0104",
                "Graduation Portrait Session",
                now.AddDays(7).Date.AddHours(14),
                now.AddDays(7).Date.AddHours(15),
                "Bryan's Studio",
                "WhatsApp inquiry. Client asked for cap-and-gown plus family photographs.",
                "WhatsApp",
                null),
            studio);
        UpdateStatus(whatsapp.Id, BookingStatus.Requested);

        Booking completed = Create(
            new CreateBookingIntakeRequest(
                null,
                "idphotos@bryanstudiosltd.com",
                "Maya Thompson",
                "maya.thompson@example.com",
                "+1-876-555-0105",
                "Corporate Headshots",
                now.AddDays(-2).Date.AddHours(10),
                now.AddDays(-2).Date.AddHours(12),
                "Client Office - Spanish Town",
                "Staff entered a corporate headshot session directly in Aegis.",
                "Manual",
                null),
            studio);
        UpdateStatus(completed.Id, BookingStatus.Completed);
        RecordPayment(
            completed.Id,
            new RecordPaymentConfirmationRequest(
                18000m,
                "JMD",
                "BANK-0904-0811",
                "Bank Transfer",
                "Manual",
                now.AddDays(-3)),
            studio);
    }

    private static BookingSource ParseSource(string? source)
    {
        return Enum.TryParse(source, true, out BookingSource value) ? value : BookingSource.Manual;
    }
}

public sealed record CreateBookingIntakeRequest(Guid? WorkspaceId, string? Destination, string ClientName, string ClientEmail, string? ClientPhone, string SessionType, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, string? Notes, string? Source, string? ExternalBookingId);
public sealed record UpdateBookingStatusRequest(BookingStatus Status);
public sealed record RecordPaymentConfirmationRequest(decimal Amount, string Currency, string Reference, string Provider, string? Source, DateTimeOffset ConfirmedAt);
