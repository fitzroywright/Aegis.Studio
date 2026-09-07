using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Npgsql;

namespace Aegis.Studio.Web.Bookings;

public sealed class BookingStore
{
    private readonly StudioDatabase database;

    public BookingStore(StudioDatabase database)
    {
        this.database = database;
    }

    public IReadOnlyList<Booking> GetAll()
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, job_id, workspace_id, client_name, client_email, client_phone, session_type, starts_at_utc, ends_at_utc, location, notes, source, status, external_booking_id, created_at_utc from studio_bookings order by starts_at_utc";
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<Booking> values = [];
        while (reader.Read())
        {
            values.Add(ReadBooking(reader));
        }
        return values;
    }

    public Booking? Get(Guid id)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, job_id, workspace_id, client_name, client_email, client_phone, session_type, starts_at_utc, ends_at_utc, location, notes, source, status, external_booking_id, created_at_utc from studio_bookings where id=$1";
        command.Parameters.AddWithValue(id);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadBooking(reader) : null;
    }

    public Booking Create(CreateBookingIntakeRequest request, StudioStore store)
    {
        if (!string.IsNullOrWhiteSpace(request.ExternalBookingId))
        {
            Booking? existing = FindByExternalId(request.ExternalBookingId.Trim());
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

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            insert into studio_bookings(id, job_id, workspace_id, client_name, client_email, client_phone, session_type, starts_at_utc, ends_at_utc, location, notes, source, status, external_booking_id, created_at_utc)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15)
            """;
        command.Parameters.AddWithValue(booking.Id);
        command.Parameters.AddWithValue(booking.JobId);
        command.Parameters.AddWithValue(booking.WorkspaceId);
        command.Parameters.AddWithValue(booking.ClientName);
        command.Parameters.AddWithValue(booking.ClientEmail);
        command.Parameters.AddWithValue((object?)booking.ClientPhone ?? DBNull.Value);
        command.Parameters.AddWithValue(booking.SessionType);
        command.Parameters.AddWithValue(booking.StartsAt);
        command.Parameters.AddWithValue(booking.EndsAt);
        command.Parameters.AddWithValue((object?)booking.Location ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)booking.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue((int)booking.Source);
        command.Parameters.AddWithValue((int)booking.Status);
        command.Parameters.AddWithValue((object?)booking.ExternalBookingId ?? DBNull.Value);
        command.Parameters.AddWithValue(booking.CreatedAt);
        command.ExecuteNonQuery();
        return booking;
    }

    public Booking UpdateStatus(Guid id, BookingStatus status)
    {
        if (Get(id) is null)
        {
            throw new KeyNotFoundException("Booking was not found.");
        }
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "update studio_bookings set status=$2 where id=$1";
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue((int)status);
        command.ExecuteNonQuery();
        return Get(id)!;
    }

    public ExternalPaymentConfirmation RecordPayment(Guid bookingId, RecordPaymentConfirmationRequest request, StudioStore studio)
    {
        ExternalPaymentConfirmation? duplicate = FindPaymentByReference(request.Reference);
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

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "insert into studio_booking_payments(id, booking_id, amount, currency, reference, provider, source, confirmed_at_utc, received_at_utc) values ($1,$2,$3,$4,$5,$6,$7,$8,$9)";
        command.Parameters.AddWithValue(payment.Id);
        command.Parameters.AddWithValue(payment.BookingId);
        command.Parameters.AddWithValue(payment.Amount);
        command.Parameters.AddWithValue(payment.Currency);
        command.Parameters.AddWithValue(payment.Reference);
        command.Parameters.AddWithValue(payment.Provider);
        command.Parameters.AddWithValue(payment.Source);
        command.Parameters.AddWithValue(payment.ConfirmedAt);
        command.Parameters.AddWithValue(payment.ReceivedAt);
        command.ExecuteNonQuery();
        studio.AddFinancialEvent(booking.JobId, new AddFinancialEventRequest("PaymentConfirmation", payment.Amount, $"{payment.Source} / {payment.Provider} / {payment.Reference}"));
        return payment;
    }

    public IReadOnlyList<ExternalPaymentConfirmation> GetPayments(Guid bookingId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, booking_id, amount, currency, reference, provider, source, confirmed_at_utc, received_at_utc from studio_booking_payments where booking_id=$1 order by confirmed_at_utc";
        command.Parameters.AddWithValue(bookingId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        List<ExternalPaymentConfirmation> values = [];
        while (reader.Read())
        {
            values.Add(ReadPayment(reader));
        }
        return values;
    }

    private Booking? FindByExternalId(string externalId)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, job_id, workspace_id, client_name, client_email, client_phone, session_type, starts_at_utc, ends_at_utc, location, notes, source, status, external_booking_id, created_at_utc from studio_bookings where external_booking_id=$1 limit 1";
        command.Parameters.AddWithValue(externalId);
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadBooking(reader) : null;
    }

    private ExternalPaymentConfirmation? FindPaymentByReference(string reference)
    {
        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select id, booking_id, amount, currency, reference, provider, source, confirmed_at_utc, received_at_utc from studio_booking_payments where lower(reference)=lower($1) limit 1";
        command.Parameters.AddWithValue(reference.Trim());
        using NpgsqlDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadPayment(reader) : null;
    }

    private static Booking ReadBooking(NpgsqlDataReader reader)
        => new(reader.GetGuid(0))
        {
            JobId = reader.GetGuid(1),
            WorkspaceId = reader.GetGuid(2),
            ClientName = reader.GetString(3),
            ClientEmail = reader.GetString(4),
            ClientPhone = reader.IsDBNull(5) ? null : reader.GetString(5),
            SessionType = reader.GetString(6),
            StartsAt = reader.GetFieldValue<DateTimeOffset>(7),
            EndsAt = reader.GetFieldValue<DateTimeOffset>(8),
            Location = reader.IsDBNull(9) ? null : reader.GetString(9),
            Notes = reader.IsDBNull(10) ? null : reader.GetString(10),
            Source = (BookingSource)reader.GetInt32(11),
            Status = (BookingStatus)reader.GetInt32(12),
            ExternalBookingId = reader.IsDBNull(13) ? null : reader.GetString(13),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(14)
        };

    private static ExternalPaymentConfirmation ReadPayment(NpgsqlDataReader reader)
        => new(reader.GetGuid(0))
        {
            BookingId = reader.GetGuid(1),
            Amount = reader.GetDecimal(2),
            Currency = reader.GetString(3),
            Reference = reader.GetString(4),
            Provider = reader.GetString(5),
            Source = reader.GetString(6),
            ConfirmedAt = reader.GetFieldValue<DateTimeOffset>(7),
            ReceivedAt = reader.GetFieldValue<DateTimeOffset>(8)
        };

    private static BookingSource ParseSource(string? source) => Enum.TryParse(source, true, out BookingSource value) ? value : BookingSource.Manual;
}

public sealed record CreateBookingIntakeRequest(Guid? WorkspaceId, string? Destination, string ClientName, string ClientEmail, string? ClientPhone, string SessionType, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, string? Notes, string? Source, string? ExternalBookingId);
public sealed record UpdateBookingStatusRequest(BookingStatus Status);
public sealed record RecordPaymentConfirmationRequest(decimal Amount, string Currency, string Reference, string Provider, string? Source, DateTimeOffset ConfirmedAt);
