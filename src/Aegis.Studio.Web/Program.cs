using System.Text.Json.Serialization;
using Aegis.Pos.Core;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Demo;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Pos;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
MicrosoftGraphEmailOptions graphEmailOptions = builder.Configuration.GetSection("MicrosoftGraphEmail").Get<MicrosoftGraphEmailOptions>() ?? new MicrosoftGraphEmailOptions();
ActiveDirectoryAuthenticationOptions activeDirectoryOptions = builder.Configuration.GetSection("Authentication:ActiveDirectory").Get<ActiveDirectoryAuthenticationOptions>() ?? throw new InvalidOperationException("Authentication:ActiveDirectory must be configured.");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.Cookie.Name = "Aegis.Studio.Auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.LoginPath = "/login"; options.AccessDeniedPath = "/login"; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true; options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; } context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; }; });
builder.Services.AddAuthorization(options => { options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });
builder.Services.AddHealthChecks(); builder.Services.AddSingleton(activeDirectoryOptions); builder.Services.AddSingleton<IAuthenticator, ActiveDirectory>(); builder.Services.AddSingleton(graphEmailOptions); builder.Services.AddSingleton<MicrosoftGraphEmailClient>(); builder.Services.AddHostedService<InboundEmailWorker>(); builder.Services.AddHostedService<OutboundEmailWorker>(); builder.Services.AddSingleton<DemoStore>(); builder.Services.AddSingleton<BookingStore>(); builder.Services.AddSingleton<StudioPosStore>(); builder.Services.AddSingleton<AttachmentStore>(); builder.Services.AddSingleton<OutboundEmailStore>(); builder.Services.AddSingleton<UnroutedEmailStore>(); builder.Services.AddSingleton<IInboundEmailRouter, ConfiguredEmailRouter>();
WebApplication app = builder.Build();
app.UseAuthentication(); app.Use(async (context, next) => { bool document = context.Request.Path == "/" || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase); if (document && context.User.Identity?.IsAuthenticated != true) { context.Response.Redirect("/login"); return; } await next(); }); app.UseAuthorization(); app.UseStaticFiles();
app.MapGet("/login", (IWebHostEnvironment e) => Results.File(Path.Combine(e.WebRootPath, "login.html"), "text/html; charset=utf-8")).AllowAnonymous();
app.MapGet("/", (IWebHostEnvironment e) => Results.File(Path.Combine(e.WebRootPath, "index.html"), "text/html; charset=utf-8")).RequireAuthorization();
app.MapStudioAuthentication(); app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/api/email/status", (MicrosoftGraphEmailClient client, MicrosoftGraphEmailOptions options, UnroutedEmailStore unrouted) => Results.Ok(new { configured = client.IsConfigured, collectionMailbox = options.Mailbox, pollSeconds = (int)client.PollInterval.TotalSeconds, mode = client.IsConfigured ? "Microsoft Graph" : "Manual/Test", unroutedCount = unrouted.GetOpen().Count }));
app.MapGet("/api/workspaces", (DemoStore store) => Results.Ok(store.GetWorkspaces()));
app.MapGet("/api/conversations", (Guid? workspaceId, DemoStore store) => Results.Ok(store.GetConversations(workspaceId)));
app.MapGet("/api/conversations/{id:guid}", (Guid id, DemoStore store) => store.GetConversation(id) is DemoConversation value ? Results.Ok(value) : Results.NotFound());
app.MapPost("/api/interactions", (IncomingInteractionRequest request, DemoStore store) => string.IsNullOrWhiteSpace(request.ContactName) || string.IsNullOrWhiteSpace(request.ContactAddress) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body) ? Results.BadRequest(new { error = "Contact, address, subject and message are required." }) : Results.Created("/api/conversations", store.ReceiveInteraction(request)));
app.MapPost("/api/email/inbound", (InboundEmail email, IInboundEmailRouter router, DemoStore store, UnroutedEmailStore unrouted) => { bool evidence = email.ToAddresses.Count > 0 || email.CcAddresses.Count > 0 || email.InternetHeaders.ContainsKey("X-Aegis-Route"); if (string.IsNullOrWhiteSpace(email.MessageId) || string.IsNullOrWhiteSpace(email.FromAddress) || !evidence || string.IsNullOrWhiteSpace(email.Subject) || string.IsNullOrWhiteSpace(email.Body)) return Results.BadRequest(new { error = "Message id, sender, routing evidence, subject and body are required." }); EmailRouteResult route = router.Route(email); if (!route.IsRouted) { UnroutedEmail item = unrouted.Capture(email, route.FailureReason ?? "No route was resolved."); return Results.UnprocessableEntity(new { routed = false, reviewId = item.Id, route.FailureReason }); } EmailIngestResult result = store.ReceiveEmail(email, route); return Results.Ok(new { routed = true, workspaceId = route.WorkspaceId, conversationId = result.Conversation.Id, result.CreatedConversation, result.WasDuplicate }); });
app.MapGet("/api/email/unrouted", (UnroutedEmailStore store) => Results.Ok(store.GetOpen()));
app.MapGet("/api/email/outbox", (OutboundEmailStore store) => Results.Ok(store.GetPending()));
app.MapPost("/api/conversations/{id:guid}/messages", (Guid id, AddMessageRequest request, DemoStore store, OutboundEmailStore outbox) => { if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Message body is required." }); DemoConversation? current = store.GetConversation(id); if (current is null) return Results.NotFound(); DemoConversation updated = store.AddMessage(id, request); OutboundEmail? email = !request.IsInternal && string.Equals(updated.Channel, "Email", StringComparison.OrdinalIgnoreCase) ? outbox.Queue(updated, request.Body) : null; return Results.Ok(new { conversation = updated, outboundEmail = email }); });
app.MapPost("/api/conversations/{id:guid}/jobs", (Guid id, CreateJobRequest request, DemoStore store) => string.IsNullOrWhiteSpace(request.Title) ? Results.BadRequest(new { error = "Job title is required." }) : store.GetConversation(id) is null ? Results.NotFound() : Results.Ok(store.CreateJob(id, request)));
app.MapGet("/api/jobs", (DemoStore store) => Results.Ok(store.GetJobs())); app.MapGet("/api/jobs/{id:guid}", (Guid id, DemoStore store) => store.GetJob(id) is DemoJob job ? Results.Ok(job) : Results.NotFound()); app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, DemoStore store) => store.GetJob(id) is null ? Results.NotFound() : Results.Ok(store.UpdateJob(id, request)));

app.MapGet("/api/bookings", (BookingStore store) => Results.Ok(store.GetAll()));
RouteHandlerBuilder bookingDetailEndpoint = app.MapGet("/api/bookings/{id:guid}", (Guid id, BookingStore store) => store.Get(id) is Booking booking ? Results.Ok(new { booking, payments = store.GetPayments(id) }) : Results.NotFound());
RouteHandlerBuilder bookingCreateEndpoint = app.MapPost("/api/bookings", (CreateBookingIntakeRequest request, BookingStore bookings, DemoStore studio) => { if (string.IsNullOrWhiteSpace(request.ClientName) || string.IsNullOrWhiteSpace(request.SessionType) || request.EndsAt <= request.StartsAt) return Results.BadRequest(new { error = "Client name, session type and a valid time range are required." }); Booking booking = bookings.Create(request, studio); return Results.Created($"/api/bookings/{booking.Id}", new { booking, job = studio.GetJob(booking.JobId) }); });
app.MapPut("/api/bookings/{id:guid}/status", (Guid id, UpdateBookingStatusRequest request, BookingStore store) => store.Get(id) is null ? Results.NotFound() : Results.Ok(store.UpdateStatus(id, request.Status)));
RouteHandlerBuilder paymentConfirmationEndpoint = app.MapPost("/api/bookings/{id:guid}/payment-confirmations", (Guid id, RecordPaymentConfirmationRequest request, BookingStore store, DemoStore studio) => { Booking? booking = store.Get(id); if (booking is null) return Results.NotFound(); if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.Currency) || string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Provider)) return Results.BadRequest(new { error = "Positive amount, currency, payment reference and provider are required." }); return Results.Ok(store.RecordPayment(id, request, studio)); });
if (app.Environment.IsDevelopment())
{
    bookingDetailEndpoint.AllowAnonymous();
    bookingCreateEndpoint.AllowAnonymous();
    paymentConfirmationEndpoint.AllowAnonymous();
}

app.MapGet("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, DemoStore studio, StudioPosStore pos) => studio.GetJob(jobId) is null ? Results.NotFound() : Results.Ok(pos.GetForJob(jobId)));
app.MapPost("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, CreateStudioSaleRequest request, DemoStore studio, StudioPosStore pos) =>
{
    if (studio.GetJob(jobId) is null) return Results.NotFound();
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.UnitPrice < 0m || x.Quantity <= 0)) return Results.BadRequest(new { error = "At least one valid sale line is required." });
    try { Sale sale = pos.Create(jobId, request, studio); return Results.Created($"/api/pos/sales/{sale.Id}", sale); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/pos/sales/{saleId:guid}", (Guid saleId, StudioPosStore pos) => pos.Get(saleId) is Sale sale ? Results.Ok(sale) : Results.NotFound());
app.MapPost("/api/pos/sales/{saleId:guid}/complete", (Guid saleId, CompleteStudioSaleRequest request, HttpContext context, DemoStore studio, StudioPosStore pos) =>
{
    if (pos.Get(saleId) is null) return Results.NotFound();
    string userId = context.User.Identity?.Name ?? "Aegis.Studio";
    try { return Results.Ok(pos.Complete(saleId, request, studio, userId)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/pos/sales/{saleId:guid}/void", (Guid saleId, VoidStudioSaleRequest request, HttpContext context, StudioPosStore pos) =>
{
    if (pos.Get(saleId) is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { error = "Void reason is required." });
    try { return Results.Ok(pos.Void(saleId, context.User.Identity?.Name ?? "Aegis.Studio", request.Reason)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/pos/sales/{saleId:guid}/refunds", (Guid saleId, RefundStudioSaleRequest request, DemoStore studio, StudioPosStore pos) =>
{
    if (pos.Get(saleId) is null) return Results.NotFound();
    try { return Results.Ok(pos.Refund(saleId, request, studio)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/pos/device-agent", (IConfiguration configuration) => Results.Ok(new { baseUrl = configuration["PosDeviceAgent:BaseUrl"] ?? "https://localhost:7399" }));

app.MapGet("/api/conversations/{id:guid}/attachments", (Guid id, DemoStore store, AttachmentStore attachments) => store.GetConversation(id) is null ? Results.NotFound() : Results.Ok(attachments.GetForConversation(id)));
app.MapPost("/api/conversations/{id:guid}/attachments", async (Guid id, IFormFile file, string? visibility, DemoStore store, AttachmentStore attachments, CancellationToken token) => { if (store.GetConversation(id) is null) return Results.NotFound(); if (file.Length == 0 || file.Length > 100L * 1024L * 1024L) return Results.BadRequest(new { error = "Attachment must be between 1 byte and 100 MB." }); DemoAttachment attachment = await attachments.AddAsync(id, file, visibility ?? "External", token); return Results.Created($"/api/attachments/{attachment.Id}", attachment); }).DisableAntiforgery();
app.MapGet("/api/attachments/{id:guid}", (Guid id, AttachmentStore attachments) => { DemoAttachment? attachment = attachments.Get(id); if (attachment is null) return Results.NotFound(); string path = attachments.GetPhysicalPath(attachment); return File.Exists(path) ? Results.File(path, attachment.ContentType, attachment.FileName) : Results.NotFound(); });
app.MapGet("/api/accounts/summary", (DemoStore store) => Results.Ok(store.GetAccountsSummary()));
app.MapFallbackToFile("index.html").RequireAuthorization(); app.Run();
