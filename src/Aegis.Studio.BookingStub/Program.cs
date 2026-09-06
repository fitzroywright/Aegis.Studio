using System.Net.Http.Json;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("Aegis", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Aegis:BaseUrl"] ?? "https://localhost:7248");
});

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/stub/bookings", async (HttpRequest request, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    object? payload = await request.ReadFromJsonAsync<object>(cancellationToken);
    HttpResponseMessage response = await clients.CreateClient("Aegis").PostAsJsonAsync("/api/bookings", payload, cancellationToken);
    string body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(body, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
});

app.MapPost("/stub/bookings/{bookingId:guid}/payments", async (Guid bookingId, HttpRequest request, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    object? payload = await request.ReadFromJsonAsync<object>(cancellationToken);
    HttpResponseMessage response = await clients.CreateClient("Aegis").PostAsJsonAsync($"/api/bookings/{bookingId}/payment-confirmations", payload, cancellationToken);
    string body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(body, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
});

app.MapGet("/stub/bookings/{bookingId:guid}", async (Guid bookingId, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    HttpResponseMessage response = await clients.CreateClient("Aegis").GetAsync($"/api/bookings/{bookingId}", cancellationToken);
    string body = await response.Content.ReadAsStringAsync(cancellationToken);
    return Results.Content(body, response.Content.Headers.ContentType?.ToString() ?? "application/json", statusCode: (int)response.StatusCode);
});

app.MapFallbackToFile("index.html");
app.Run();
