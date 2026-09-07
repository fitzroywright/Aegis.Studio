using System.Security.Claims;
using System.Text.Json.Serialization;
using Aegis.Pos.Core;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Profiles;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Storage;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

string studioConnectionString = builder.Configuration.GetConnectionString("Studio")
    ?? throw new InvalidOperationException("ConnectionStrings:Studio must be configured.");

NpgsqlDataSource dataSource = NpgsqlDataSource.Create(studioConnectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<StudioDatabase>();
builder.Services.AddSingleton<StudioFeatureDatabase>();

MicrosoftGraphEmailOptions graphEmailOptions = builder.Configuration.GetSection("MicrosoftGraphEmail").Get<MicrosoftGraphEmailOptions>() ?? new MicrosoftGraphEmailOptions();
ActiveDirectoryAuthenticationOptions activeDirectoryOptions = builder.Configuration.GetSection("Authentication:ActiveDirectory").Get<ActiveDirectoryAuthenticationOptions>() ?? throw new InvalidOperationException("Authentication:ActiveDirectory must be configured.");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.Cookie.Name = "Aegis.Studio.Auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.LoginPath = "/login"; options.AccessDeniedPath = "/login"; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true; options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; } context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; }; });
builder.Services.AddAuthorization(options => { options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(activeDirectoryOptions);
builder.Services.AddSingleton<IAuthenticator, ActiveDirectory>();
builder.Services.AddSingleton(graphEmailOptions);
builder.Services.AddSingleton<MicrosoftGraphEmailClient>();
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<StudioStore>();
builder.Services.AddSingleton<BookingStore>();
builder.Services.AddSingleton<BookingApiAuthenticator>();
builder.Services.AddSingleton<UserProfileStore>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<ProductionStore>();
builder.Services.AddSingleton<StudioPosStore>();
builder.Services.AddStudioPosHardening();
builder.Services.AddSingleton<AttachmentStore>();
builder.Services.AddSingleton<OutboundEmailStore>();
builder.Services.AddSingleton<UnroutedEmailStore>();
builder.Services.AddSingleton<GraphEmailDeltaStateStore>();
builder.Services.AddSingleton<IInboundEmailRouter, ConfiguredEmailRouter>();
builder.Services.AddHostedService<InboundEmailWorker>();
builder.Services.AddHostedService<OutboundEmailWorker>();
builder.Services.AddHostedService<NotificationDeliveryWorker>();

WebApplication app = builder.Build();
await app.Services.GetRequiredService<StudioDatabase>().InitializeAsync();
await app.Services.GetRequiredService<StudioFeatureDatabase>().InitializeAsync();

app.UseAuthentication();
app.Use(async (context, next) => { bool document = context.Request.Path == "/" || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase); if (document && context.User.Identity?.IsAuthenticated != true) { context.Response.Redirect("/login"); return; } await next(); });
app.UseAuthorization();
app.UseStaticFiles();
app.MapGet("/login", (IWebHostEnvironment e) => Results.File(Path.Combine(e.WebRootPath, "login.html"), "text/html; charset=utf-8")).AllowAnonymous();
app.MapGet("/", (IWebHostEnvironment e) => Results.File(Path.Combine(e.WebRootPath, "index.html"), "text/html; charset=utf-8")).RequireAuthorization();
app.MapStudioAuthentication();
app.MapStudioUserProfiles();
app.MapStudioNotifications();
app.MapStudioScheduling();
app.MapStudioProduction();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapStudioPosHardening();

app.MapGet("/api/email/status", (MicrosoftGraphEmailClient client, MicrosoftGraphEmailOptions options, UnroutedEmailStore unrouted, IConfiguration configuration) => Results.Ok(new
{
    enabled = options.Enabled,
    configured = client.IsConfigured,
    collectionMailbox = options.Mailbox,
    pollSeconds = (int)client.PollInterval.TotalSeconds,
    mode = client.IsConfigured ? "Microsoft Graph" : "Microsoft Graph - waiting for client secret",
    slackConfigured = !string.IsNullOrWhiteSpace(configuration["Notifications:SlackWebhookUrl"]),
    teamsConfigured = !string.IsNullOrWhiteSpace(configuration["Notifications:TeamsWebhookUrl"]),
    unroutedCount = unrouted.GetOpen().Count
}));
app.MapGet("/api/booking-security/status", (BookingApiAuthenticator authentication) => Results.Ok(authentication.GetStatus())).RequireAuthorization();

app.MapGet("/api/workspaces", (StudioStore store) => Results.Ok(store.GetWorkspaces()));
RouteGroupBuilder workspaceAdmin = app.MapGroup("/api/admin/workspaces").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
workspaceAdmin.MapGet("/", (WorkspaceStore store) => Results.Ok(store.GetAll(true)));
workspaceAdmin.MapPost("/", (SaveWorkspaceRequest request, WorkspaceStore store) => string.IsNullOrWhiteSpace(request.Name) ? Results.BadRequest(new { error = "Workspace name is required." }) : Results.Created("/api/admin/workspaces", store.Create(request)));
workspaceAdmin.MapPut("/{id:guid}", (Guid id, SaveWorkspaceRequest request, WorkspaceStore store) => { try { return Results.Ok(store.Update(id, request)); } catch (KeyNotFoundException) { return Results.NotFound(); } });

app.MapGet("/api/conversations", (Guid? workspaceId, StudioStore store) => Results.Ok(store.GetConversations(workspaceId)));
app.MapGet("/api/conversations/{id:guid}", (Guid id, StudioStore store) => store.GetConversation(id) is StudioConversation value ? Results.Ok(value) : Results.NotFound());
app.MapPost("/api/interactions", (IncomingInteractionRequest request, StudioStore store, NotificationService notifications) =>
{
    if (string.IsNullOrWhiteSpace(request.ContactName) || string.IsNullOrWhiteSpace(request.ContactAddress) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Contact, address, subject and message are required." });
    try { StudioConversation conversation = store.ReceiveInteraction(request); notifications.PublishToAll("WorkspaceMessage", $"New request: {conversation.Subject}", $"{conversation.ContactName} sent a new {conversation.Channel} request.", "Conversation", conversation.Id.ToString("D")); return Results.Created("/api/conversations", conversation); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/email/inbound", (InboundEmail email, IInboundEmailRouter router, StudioStore store, UnroutedEmailStore unrouted, NotificationService notifications) =>
{
    bool evidence = email.ToAddresses.Count > 0 || email.CcAddresses.Count > 0 || email.InternetHeaders.ContainsKey("X-Aegis-Route");
    if (string.IsNullOrWhiteSpace(email.MessageId) || string.IsNullOrWhiteSpace(email.FromAddress) || !evidence || string.IsNullOrWhiteSpace(email.Subject) || string.IsNullOrWhiteSpace(email.Body)) return Results.BadRequest(new { error = "Message id, sender, routing evidence, subject and body are required." });
    EmailRouteResult route = router.Route(email);
    if (!route.IsRouted) { UnroutedEmail item = unrouted.Capture(email, route.FailureReason ?? "No route was resolved."); return Results.UnprocessableEntity(new { routed = false, reviewId = item.Id, route.FailureReason }); }
    EmailIngestResult result = store.ReceiveEmail(email, route);
    if (!result.WasDuplicate) notifications.PublishToAll("WorkspaceMessage", $"Email: {email.Subject}", $"New email from {email.FromName} routed to {route.WorkspaceName}.", "Conversation", result.Conversation.Id.ToString("D"));
    return Results.Ok(new { routed = true, workspaceId = route.WorkspaceId, conversationId = result.Conversation.Id, result.CreatedConversation, result.WasDuplicate });
});
app.MapGet("/api/email/unrouted", (UnroutedEmailStore store) => Results.Ok(store.GetOpen()));
app.MapGet("/api/email/outbox", (OutboundEmailStore store) => Results.Ok(store.GetPending()));
app.MapPost("/api/conversations/{id:guid}/messages", (Guid id, AddMessageRequest request, ClaimsPrincipal user, StudioStore store, OutboundEmailStore outbox, NotificationService notifications) =>
{
    if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Message body is required." });
    StudioConversation? current = store.GetConversation(id); if (current is null) return Results.NotFound();
    StudioConversation updated = store.AddMessage(id, request, user);
    OutboundEmail? email = !request.IsInternal && string.Equals(updated.Channel, "Email", StringComparison.OrdinalIgnoreCase) ? outbox.Queue(updated, request.Body) : null;
    if (request.IsInternal) notifications.PublishToAll("Mention", $"Internal note: {updated.Subject}", request.Body, "Conversation", id.ToString("D"));
    return Results.Ok(new { conversation = updated, outboundEmail = email });
});
app.MapPost("/api/conversations/{id:guid}/jobs", (Guid id, CreateJobRequest request, StudioStore store) => string.IsNullOrWhiteSpace(request.Title) ? Results.BadRequest(new { error = "Job title is required." }) : store.GetConversation(id) is null ? Results.NotFound() : Results.Ok(store.CreateJob(id, request)));
app.MapGet("/api/jobs", (StudioStore store) => Results.Ok(store.GetJobs()));
app.MapGet("/api/jobs/{id:guid}", (Guid id, StudioStore store) => store.GetJob(id) is StudioJob job ? Results.Ok(job) : Results.NotFound());
app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, StudioStore store, NotificationService notifications) =>
{
    StudioJob? before = store.GetJob(id); if (before is null) return Results.NotFound();
    StudioJob updated = store.UpdateJob(id, request);
    if (!string.Equals(before.Status, updated.Status, StringComparison.OrdinalIgnoreCase)) notifications.PublishToAll("JobStatusChanged", $"Job status: {updated.Title}", $"{before.Status} → {updated.Status}", "Job", id.ToString("D"));
    if (!string.Equals(before.AssignedTo, updated.AssignedTo, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(updated.AssignedTo)) notifications.PublishToUser(updated.AssignedTo, "JobAssigned", $"Job assigned: {updated.Title}", $"You were assigned to {updated.Title} for {updated.ClientName}.", "Job", id.ToString("D"));
    return Results.Ok(updated);
});

app.MapGet("/api/bookings", (BookingStore store) => Results.Ok(store.GetAll()));
app.MapGet("/api/bookings/{id:guid}", (Guid id, HttpContext context, BookingStore store, BookingApiAuthenticator security) => { if (!security.IsAuthorized(context)) return Results.Unauthorized(); return store.Get(id) is Booking booking ? Results.Ok(new { booking, payments = store.GetPayments(id) }) : Results.NotFound(); }).AllowAnonymous();
app.MapPost("/api/bookings", (CreateBookingIntakeRequest request, HttpContext context, BookingStore bookings, StudioStore studio, BookingApiAuthenticator security, NotificationService notifications) =>
{
    if (!security.IsAuthorized(context)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(request.ClientName) || string.IsNullOrWhiteSpace(request.SessionType) || request.EndsAt <= request.StartsAt) return Results.BadRequest(new { error = "Client name, session type and a valid time range are required." });
    try { Booking booking = bookings.Create(request, studio); notifications.PublishToAll("BookingChanged", $"New booking: {booking.SessionType}", $"{booking.ClientName} requested {booking.StartsAt.LocalDateTime:g}.", "Booking", booking.Id.ToString("D")); return Results.Created($"/api/bookings/{booking.Id}", new { booking, job = studio.GetJob(booking.JobId) }); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).AllowAnonymous();
app.MapPut("/api/bookings/{id:guid}/status", (Guid id, UpdateBookingStatusRequest request, BookingStore store, NotificationService notifications) =>
{
    if (store.Get(id) is null) return Results.NotFound(); Booking booking = store.UpdateStatus(id, request.Status); notifications.PublishToAll("BookingChanged", $"Booking status: {booking.SessionType}", $"Booking for {booking.ClientName} is now {booking.Status}.", "Booking", id.ToString("D")); return Results.Ok(booking);
});
app.MapPost("/api/bookings/{id:guid}/payment-confirmations", (Guid id, RecordPaymentConfirmationRequest request, HttpContext context, BookingStore store, StudioStore studio, BookingApiAuthenticator security, NotificationService notifications) =>
{
    if (!security.IsAuthorized(context)) return Results.Unauthorized();
    Booking? booking = store.Get(id); if (booking is null) return Results.NotFound();
    if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Provider)) return Results.BadRequest(new { error = "Positive amount, currency, payment reference and provider are required." });
    ExternalPaymentConfirmation payment = store.RecordPayment(id, request, studio); notifications.PublishToAll("PaymentChanged", $"Payment received: {booking.ClientName}", $"{payment.Currency} {payment.Amount:N2} via {payment.Provider}.", "Booking", id.ToString("D")); return Results.Ok(payment);
}).AllowAnonymous();

app.MapGet("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, StudioStore studio, StudioPosStore pos) => studio.GetJob(jobId) is null ? Results.NotFound() : Results.Ok(pos.GetForJob(jobId)));
app.MapPost("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, CreateStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos) => { if (studio.GetJob(jobId) is null) return Results.NotFound(); if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.UnitPrice < 0m || x.Quantity <= 0)) return Results.BadRequest(new { error = "At least one valid sale line is required." }); try { Sale sale = pos.Create(jobId, request, studio, context.User.Identity?.Name ?? "Studio"); return Results.Created($"/api/pos/sales/{sale.Id}", sale); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapGet("/api/pos/sales/{saleId:guid}", (Guid saleId, StudioPosStore pos) => pos.Get(saleId) is Sale sale ? Results.Ok(sale) : Results.NotFound());
app.MapPost("/api/pos/sales/{saleId:guid}/complete", (Guid saleId, CompleteStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos) => { if (pos.Get(saleId) is null) return Results.NotFound(); string userId = context.User.Identity?.Name ?? "Studio"; try { return Results.Ok(pos.Complete(saleId, request, studio, userId)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/pos/sales/{saleId:guid}/void", (Guid saleId, VoidStudioSaleRequest request, HttpContext context, StudioPosStore pos) => { if (pos.Get(saleId) is null) return Results.NotFound(); if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { error = "Void reason is required." }); try { return Results.Ok(pos.Void(saleId, context.User.Identity?.Name ?? "Studio", request.Reason)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/pos/sales/{saleId:guid}/refunds", (Guid saleId, RefundStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos) => { if (pos.Get(saleId) is null) return Results.NotFound(); try { return Results.Ok(pos.Refund(saleId, request, studio, context.User.Identity?.Name ?? "Studio")); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapGet("/api/pos/device-agent", (IConfiguration configuration) => Results.Ok(new { baseUrl = configuration["PosDeviceAgent:BaseUrl"] ?? "https://localhost:7399" }));

app.MapGet("/api/conversations/{id:guid}/attachments", (Guid id, StudioStore store, AttachmentStore attachments) => store.GetConversation(id) is null ? Results.NotFound() : Results.Ok(attachments.GetForConversation(id)));
app.MapPost("/api/conversations/{id:guid}/attachments", async (Guid id, IFormFile file, string? visibility, ClaimsPrincipal user, StudioStore store, AttachmentStore attachments, CancellationToken token) =>
{
    if (store.GetConversation(id) is null) return Results.NotFound();
    try { StudioAttachment attachment = await attachments.AddAsync(id, file, visibility ?? "External", user.Identity?.Name ?? "Unknown", token); return Results.Created($"/api/attachments/{attachment.Id}", attachment); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).DisableAntiforgery();
app.MapGet("/api/attachments/{id:guid}", (Guid id, AttachmentStore attachments) => { StudioAttachment? attachment = attachments.Get(id); if (attachment is null) return Results.NotFound(); string path = attachments.GetPhysicalPath(attachment); return File.Exists(path) ? Results.File(path, attachment.ContentType, attachment.FileName, enableRangeProcessing: true) : Results.NotFound(); });

app.MapGet("/api/accounts/summary", (StudioStore store) => Results.Ok(store.GetAccountsSummary()));
app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();
