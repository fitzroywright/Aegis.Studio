using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Application.Bookings;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Application.Operations;
using Aegis.Studio.Application.Workspaces;
using Aegis.Studio.Domain.Communications;
using Aegis.Studio.Domain.Operations;
using Aegis.Studio.Domain.Workspaces;
using Aegis.Studio.Infrastructure;
using Aegis.Studio.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<BookingManagementService>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddScoped<WorkspaceAccessService>();
builder.Services.AddScoped<WorkspaceDashboardService>();
builder.Services.AddScoped<EscalationService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHealthChecks().AddDbContextCheck<StudioDbContext>("database");

WebApplication app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

using (IServiceScope scope = app.Services.CreateScope())
{
    DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

app.MapGet("/api/catalog", async (IStudioCatalogRepository catalog, ICommunicationRepository communications, CancellationToken cancellationToken) => Results.Ok(new { services = await catalog.GetServicesAsync(cancellationToken), photographers = await catalog.GetPhotographersAsync(cancellationToken), workspaces = await communications.GetWorkspacesAsync(cancellationToken) }));
app.MapGet("/api/bookings", async (IBookingRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetUpcomingAsync(cancellationToken)));
app.MapGet("/api/bookings/{bookingId:guid}", async (Guid bookingId, IBookingRepository repository, CancellationToken cancellationToken) => (await repository.GetAsync(bookingId, cancellationToken)) is { } booking ? Results.Ok(booking) : Results.NotFound());
app.MapPost("/api/bookings", async (CreateBookingRequest request, BookingService service, CancellationToken cancellationToken) => Results.Ok(await service.CreateAsync(request, cancellationToken)));
app.MapPost("/api/bookings/{bookingId:guid}/reschedule", async (Guid bookingId, RescheduleBookingRequest request, BookingManagementService service, CancellationToken cancellationToken) => Results.Ok(await service.RescheduleAsync(bookingId, request, cancellationToken)));
app.MapPost("/api/bookings/{bookingId:guid}/cancel", async (Guid bookingId, BookingManagementService service, CancellationToken cancellationToken) => Results.Ok(await service.CancelAsync(bookingId, cancellationToken)));
app.MapPost("/api/bookings/{bookingId:guid}/complete", async (Guid bookingId, BookingManagementService service, CancellationToken cancellationToken) => Results.Ok(await service.CompleteAsync(bookingId, cancellationToken)));

app.MapGet("/api/workspaces", async (ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetWorkspacesAsync(cancellationToken)));
app.MapGet("/api/workspaces/{workspaceId:guid}/dashboard", async (Guid workspaceId, WorkspaceDashboardService service, CancellationToken cancellationToken) => Results.Ok(await service.GetAsync(workspaceId, cancellationToken)));
app.MapGet("/api/workspaces/{workspaceId:guid}/members", async (Guid workspaceId, IWorkspaceRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetMembersAsync(workspaceId, cancellationToken)));
app.MapPost("/api/workspaces/{workspaceId:guid}/members", async (Guid workspaceId, AddWorkspaceMemberRequest request, IWorkspaceRepository repository, IUnitOfWork unitOfWork, CancellationToken cancellationToken) => { WorkspaceMember member = new(workspaceId, request.UserId, request.DisplayName, request.Role); await repository.AddMemberAsync(member, cancellationToken); await unitOfWork.SaveChangesAsync(cancellationToken); return Results.Ok(member); });
app.MapGet("/api/workspaces/{workspaceId:guid}/alerts", async (Guid workspaceId, IOperationsRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetOpenAlertsAsync(workspaceId, cancellationToken)));
app.MapPost("/api/alerts/{alertId:guid}/acknowledge", async (Guid alertId, IOperationsRepository repository, IUnitOfWork unitOfWork, CancellationToken cancellationToken) => { OperationalAlert? alert = await repository.GetAlertAsync(alertId, cancellationToken); if (alert is null) return Results.NotFound(); alert.Acknowledge(); await unitOfWork.SaveChangesAsync(cancellationToken); return Results.NoContent(); });

app.MapGet("/api/workspaces/{workspaceId:guid}/conversations", async (Guid workspaceId, ConversationFolder? folder, string? search, ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetWorkspaceConversationsAsync(workspaceId, folder ?? ConversationFolder.Inbox, search, cancellationToken)));
app.MapGet("/api/conversations/{conversationId:guid}", async (Guid conversationId, CommunicationService service, CancellationToken cancellationToken) => Results.Ok(await service.GetDetailsAsync(conversationId, cancellationToken)));
app.MapGet("/api/conversations/{conversationId:guid}/messages", async (Guid conversationId, ICommunicationRepository repository, CancellationToken cancellationToken) => Results.Ok(await repository.GetMessagesAsync(conversationId, cancellationToken)));
app.MapPost("/api/conversations/{conversationId:guid}/read", async (Guid conversationId, CommunicationService service, CancellationToken cancellationToken) => { await service.MarkReadAsync(conversationId, cancellationToken); return Results.NoContent(); });
app.MapPost("/api/conversations/{conversationId:guid}/reply", async (Guid conversationId, ReplyRequest request, CommunicationService service, CancellationToken cancellationToken) => { await service.ReplyAsync(conversationId, request.Body, cancellationToken); return Results.NoContent(); });
app.MapPost("/api/conversations/{conversationId:guid}/notes", async (Guid conversationId, InternalNoteRequest request, CommunicationService service, CancellationToken cancellationToken) => { await service.AddInternalNoteAsync(conversationId, request.Body, request.Author, cancellationToken); return Results.NoContent(); });
app.MapPost("/api/conversations/{conversationId:guid}/assign", async (Guid conversationId, AssignConversationRequest request, CommunicationService service, CancellationToken cancellationToken) => { await service.AssignAsync(conversationId, request.UserId, cancellationToken); return Results.NoContent(); });
app.MapPost("/api/conversations/{conversationId:guid}/status", async (Guid conversationId, UpdateConversationRequest request, CommunicationService service, CancellationToken cancellationToken) => { await service.SetStatusAsync(conversationId, request.Status, cancellationToken); return Results.NoContent(); });
app.MapPost("/api/dev/inbound", async (InboundMailItem request, CommunicationService service, CancellationToken cancellationToken) => Results.Ok(await service.RouteInboundAsync(request, cancellationToken)));
app.MapPost("/api/operations/evaluate-escalations", async (EscalationService service, CancellationToken cancellationToken) => Results.Ok(new { created = await service.EvaluateAsync(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), cancellationToken) }));

app.MapHealthChecks("/diagnostics/health");
app.MapGet("/diagnostics/starfleet", () => Results.Ok(new { level = "GREEN", application = "Aegis.Studio", checks = new[] { "web", "database", "communications-hub", "workspace-routing", "booking-management", "escalations" }, utc = DateTimeOffset.UtcNow }));
app.Run();

public sealed record ReplyRequest(string Body);
public sealed record AddWorkspaceMemberRequest(string UserId, string DisplayName, WorkspaceRole Role);
