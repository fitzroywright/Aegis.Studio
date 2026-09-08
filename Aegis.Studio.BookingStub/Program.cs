using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("Studio", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Studio:BaseUrl"] ?? "https://localhost:7248");
});

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/stub/bookings", async (HttpRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    object? payload = await request.ReadFromJsonAsync<object>(cancellationToken);
    using HttpRequestMessage outbound = new(HttpMethod.Post, "/api/bookings") { Content = JsonContent.Create(payload) };
    Sign(outbound, configuration);
    using HttpResponseMessage response = await clients.CreateClient("Studio").SendAsync(outbound, cancellationToken);
    return await CopyAsync(response, cancellationToken);
});

app.MapPost("/stub/bookings/{bookingId:guid}/payments", async (Guid bookingId, HttpRequest request, IHttpClientFactory clients, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    object? payload = await request.ReadFromJsonAsync<object>(cancellationToken);
    string path = $"/api/bookings/{bookingId}/payment-confirmations";
    using HttpRequestMessage outbound = new(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
    Sign(outbound, configuration);
    using HttpResponseMessage response = await clients.CreateClient("Studio").SendAsync(outbound, cancellationToken);
    return await CopyAsync(response, cancellationToken);
});

app.MapGet("/stub/bookings/{bookingId:guid}", async (Guid bookingId, IHttpClientFactory clients, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    string path = $"/api/bookings/{bookingId}";
    using HttpRequestMessage outbound = new(HttpMethod.Get, path);
    Sign(outbound, configuration);
    using HttpResponseMessage response = await clients.CreateClient("Studio").SendAsync(outbound, cancellationToken);
    return await CopyAsync(response, cancellationToken);
});

app.MapFallbackToFile("index.html");
app.Run();

static void Sign(HttpRequestMessage request, IConfiguration configuration)
{
    string clientId = configuration["Studio:BookingClientId"] ?? throw new InvalidOperationException("Studio:BookingClientId must be configured.");
    string secret = configuration["Studio:BookingClientSecret"] ?? throw new InvalidOperationException("Studio:BookingClientSecret must be configured.");
    string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    string nonce = Guid.NewGuid().ToString("N");
    string path = request.RequestUri?.OriginalString ?? "/";
    string canonical = $"{request.Method.Method.ToUpperInvariant()}\n{path}\n{timestamp}\n{nonce}";
    string signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    request.Headers.Add("X-Studio-Client", clientId);
    request.Headers.Add("X-Studio-Timestamp", timestamp);
    request.Headers.Add("X-Studio-Nonce", nonce);
    request.Headers.Add("X-Studio-Signature", signature);
}

static async Task<IResult> CopyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
{
    string body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(body, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
}
