using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Persistence;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace Aegis.Studio.Web.Bookings;

public sealed class BookingApiAuthenticator
{
    private readonly IConfiguration configuration;
    private readonly StudioDatabase database;

    public BookingApiAuthenticator(IConfiguration configuration, StudioDatabase database)
    {
        this.configuration = configuration;
        this.database = database;
    }

    public bool IsAuthorized(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        if (!TryReadSingle(context, "X-Studio-Client", out string clientId) ||
            !TryReadSingle(context, "X-Studio-Timestamp", out string timestampText) ||
            !TryReadSingle(context, "X-Studio-Nonce", out string nonce) ||
            !TryReadSingle(context, "X-Studio-Signature", out string signature))
        {
            return false;
        }

        string? secret = configuration[$"BookingApi:Clients:{clientId}:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        if (!long.TryParse(timestampText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds))
        {
            return false;
        }

        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes) > 5)
        {
            return false;
        }

        string canonical = $"{context.Request.Method.ToUpperInvariant()}\n{context.Request.Path.Value}\n{timestampText}\n{nonce}";
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] expectedBytes = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        string expected = Convert.ToHexString(expectedBytes).ToLowerInvariant();
        byte[] suppliedBytes;
        byte[] comparisonBytes;
        try
        {
            suppliedBytes = Convert.FromHexString(signature);
            comparisonBytes = Convert.FromHexString(expected);
        }
        catch (FormatException)
        {
            return false;
        }

        if (suppliedBytes.Length != comparisonBytes.Length || !CryptographicOperations.FixedTimeEquals(suppliedBytes, comparisonBytes))
        {
            return false;
        }

        return TryConsumeNonce(clientId, nonce);
    }

    public BookingApiSecurityStatus GetStatus()
    {
        IConfigurationSection clients = configuration.GetSection("BookingApi:Clients");
        int configuredClients = clients.GetChildren().Count(child => !string.IsNullOrWhiteSpace(child["Secret"]));
        return new BookingApiSecurityStatus(configuredClients, configuredClients > 0, "HMAC-SHA256", 300);
    }

    private bool TryConsumeNonce(string clientId, string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce) || nonce.Length > 200)
        {
            return false;
        }

        using NpgsqlConnection connection = database.OpenConnection();
        using NpgsqlTransaction transaction = connection.BeginTransaction();
        using (NpgsqlCommand cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "delete from studio_booking_api_nonces where used_at_utc < now() - interval '10 minutes'";
            cleanup.ExecuteNonQuery();
        }
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "insert into studio_booking_api_nonces(client_id,nonce,used_at_utc) values($1,$2,now()) on conflict do nothing";
        command.Parameters.AddWithValue(clientId);
        command.Parameters.AddWithValue(nonce);
        bool inserted = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return inserted;
    }

    private static bool TryReadSingle(HttpContext context, string name, out string value)
    {
        value = string.Empty;
        if (!context.Request.Headers.TryGetValue(name, out StringValues values) || values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return false;
        }
        value = values[0]!.Trim();
        return true;
    }
}

public sealed record BookingApiSecurityStatus(int ConfiguredClients,bool IsReady,string Scheme,int ReplayWindowSeconds);
