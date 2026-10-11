using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Workflow;
using Common.Secrets;

namespace Aegis.Studio.Web.PublicApi;

public sealed class PublicApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed record PublicV1Error(string Code, string Message, string RequestId);

public static class PublicV1Signing
{
    public static string Canonical(string method, string pathAndQuery, string timestamp, string nonce, ReadOnlySpan<byte> body, string idempotencyKey)
        => $"{method.ToUpperInvariant()}\n{pathAndQuery}\n{timestamp}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}\n{idempotencyKey}";
    public static string Sign(string secret, string canonical)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    public static bool Verify(string secret, string canonical, string supplied)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Sign(secret, canonical)), Convert.FromHexString(supplied)); }
        catch (FormatException) { return false; }
    }
}

public sealed class PublicV1Security(IConfiguration configuration, ISecretProvider secrets, StudioDatabase database)
{
    public async Task<string> Integration(HttpContext context, string scope, byte[] body)
    {
        string client = Single(context, "X-Studio-Client");
        if (client.Length > 100 || !client.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) Deny();
        var scopes = configuration.GetSection($"BookingApi:Clients:{client}:V1Scopes").Get<string[]>() ?? [];
        if (!scopes.Contains(scope, StringComparer.Ordinal)) throw new PublicApiException(403, "scope_required", "This integration is not authorized for this operation.");
        string timestamp = Single(context, "X-Studio-Timestamp"), nonce = Single(context, "X-Studio-Nonce"), signature = Single(context, "X-Studio-Signature");
        if (nonce.Length > 128) Deny();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)) Deny();
        if (seconds < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300 || seconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 300) Deny();
        string? secret = await secrets.GetAsync($"BookingApi:Clients:{client}:Secret", context.RequestAborted);
        if (string.IsNullOrEmpty(secret) || !PublicV1Signing.Verify(secret, PublicV1Signing.Canonical(context.Request.Method, context.Request.Path.Value + context.Request.QueryString.Value, timestamp, nonce, body, context.Request.Headers["Idempotency-Key"].ToString()), signature)) Deny();
        using var c = database.OpenConnection();
        if (WorkflowStore.Scalar(c, null, "insert into studio_booking_api_nonces(client_id,nonce,used_at_utc) values($1,$2,now()) on conflict do nothing returning nonce", "v1:" + client, nonce) is null)
            throw new PublicApiException(401, "replayed_signature", "Use a fresh nonce and signature for every attempt, including retries.");
        return client;
    }
    public static string Single(HttpContext context, string header)
    {
        if (!context.Request.Headers.TryGetValue(header, out var value) || value.Count != 1 || string.IsNullOrWhiteSpace(value[0])) Deny();
        return value[0]!;
    }
    public static string IdempotencyKey(HttpContext context)
    {
        string key = Single(context, "Idempotency-Key");
        if (key.Length > 100 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new PublicApiException(400, "invalid_idempotency_key", "Use a stable key of 1–100 letters, digits, dots, hyphens or underscores.");
        return key;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new PublicApiException(401, "invalid_credentials", "A valid signed integration request is required.");
}
