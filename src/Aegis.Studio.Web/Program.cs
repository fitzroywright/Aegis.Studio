using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Bookings;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Infrastructure;
using Aegis.Studio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHealthChecks().AddDbContextCheck<StudioDbContext>("postgresql");

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

using (IServiceScope scope = app.Services.CreateScope())
{
    DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

app.MapGet("/api/catalog", async (IStudioCatalogRepository catalog, ICommunicationRepository communications, CancellationToken cancellationToken) =>
{
    return Results.Ok(new
    {
        services = await catalog.GetServicesAsync(cancellationToken),
        photographers = await catalog.GetPhotographersAsync(cancellationToken),
        workspaces = await communications.GetWorkspacesAsync(cancellationToken)
    });
});

app.MapGet("/api/bookings", async (IBookingRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetUpcomingAsync(cancellationToken)));
app.MapPost("/api/bookings", async (CreateBookingRequest request, BookingService service, CancellationToken cancellationToken) => Results.Ok(await service.CreateAsync(request, cancellationToken)));
app.MapGet("/api/workspaces", async (ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetWorkspacesAsync(cancellationToken)));
app.MapGet("/api/workspaces/{workspaceId:guid}/conversations", async (Guid workspaceId, ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetWorkspaceConversationsAsync(workspaceId, cancellationToken)));
app.MapGet("/api/conversations/{conversationId:guid}/messages", async (Guid conversationId, ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetMessagesAsync(conversationId, cancellationToken)));
app.MapPost("/api/conversations/{conversationId:guid}/reply", async (Guid conversationId, ReplyRequest request, CommunicationService service, CancellationToken cancellationToken) =>
{
    await service.ReplyAsync(conversationId, request.Body, cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/dev/inbound", async (InboundMailItem request, CommunicationService service, CancellationToken cancellationToken) => Results.Ok(await service.RouteInboundAsync(request, cancellationToken)));
app.MapHealthChecks("/diagnostics/health");
app.MapGet("/diagnostics/starfleet", () => Results.Ok(new { level = "GREEN", application = "Aegis.Studio", checks = new[] { "web", "postgresql", "communications" }, utc = DateTimeOffset.UtcNow }));
app.Run();

public sealed record ReplyRequest(string Body);
