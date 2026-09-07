WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string[] allowedOrigins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ??
[
    "https://localhost:7248",
    "http://localhost:5248"
];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Studio", policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod());
});
builder.Services.AddSingleton<DeviceState>();

WebApplication app = builder.Build();
app.UseCors("Studio");

app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));
app.MapGet("/api/devices/status", (DeviceState state) => Results.Ok(new
{
    receiptPrinter = state.ReceiptPrinterName,
    cashDrawer = state.CashDrawerName,
    mode = "simulated",
    utc = DateTimeOffset.UtcNow
}));

app.MapPost("/api/receipts/print", (PrintReceiptRequest request, DeviceState state) =>
{
    if (request.SaleId == Guid.Empty || request.Lines is null || request.Lines.Count == 0)
    {
        return Results.BadRequest(new { error = "Sale ID and receipt lines are required." });
    }

    state.LastReceipt = request with { PrintedAtUtc = DateTimeOffset.UtcNow };
    Console.WriteLine($"[Aegis POS] Receipt {request.SaleId} ({request.Total:N2} {request.Currency})");
    foreach (string line in request.Lines)
    {
        Console.WriteLine(line);
    }
    return Results.Ok(new { printed = true, simulated = true, saleId = request.SaleId, utc = DateTimeOffset.UtcNow });
});

app.MapPost("/api/cash-drawer/open", (OpenDrawerRequest request, DeviceState state) =>
{
    state.LastDrawerOpenUtc = DateTimeOffset.UtcNow;
    Console.WriteLine($"[Aegis POS] Cash drawer open requested for sale {request.SaleId}.");
    return Results.Ok(new { opened = true, simulated = true, request.SaleId, utc = state.LastDrawerOpenUtc });
});

app.Run();

public sealed class DeviceState
{
    public string ReceiptPrinterName { get; set; } = "Simulated Receipt Printer";
    public string CashDrawerName { get; set; } = "Simulated Cash Drawer";
    public PrintReceiptRequest? LastReceipt { get; set; }
    public DateTimeOffset? LastDrawerOpenUtc { get; set; }
}

public sealed record PrintReceiptRequest(
    Guid SaleId,
    string Reference,
    IReadOnlyList<string> Lines,
    decimal Total,
    string Currency,
    DateTimeOffset? PrintedAtUtc = null);

public sealed record OpenDrawerRequest(Guid SaleId);
