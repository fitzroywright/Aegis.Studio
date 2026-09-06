using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Demo;
using Aegis.Studio.Web.Email;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Options;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

MicrosoftGraphEmailOptions graphEmailOptions = builder.Configuration
    .GetSection("MicrosoftGraphEmail")
    .Get<MicrosoftGraphEmailOptions>() ?? new MicrosoftGraphEmailOptions();

ActiveDirectoryAuthenticationOptions activeDirectoryOptions = builder.Configuration
    .GetSection("Authentication:ActiveDirectory")
    .Get<ActiveDirectoryAuthenticationOptions>()
    ?? throw new InvalidOperationException("Authentication:ActiveDirectory must be configured.");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Aegis.Studio.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHealthChecks();
builder.Services.AddSingleton(activeDirectoryOptions);
builder.Services.AddSingleton<IAuthenticator, ActiveDirectory>();
builder.Services.AddSingleton(graphEmailOptions);
builder.Services.AddSingleton<MicrosoftGraphEmailClient>();
builder.Services.AddHostedService<InboundEmailWorker>();
builder.Services.AddHostedService<OutboundEmailWorker>();
builder.Services.AddSingleton<DemoStore>();
builder.Services.AddSingleton<AttachmentStore>();
builder.Services.AddSingleton<OutboundEmailStore>();
builder.Services.AddSingleton<UnroutedEmailStore>();
builder.Services.AddSingleton<IInboundEmailRouter, ConfiguredEmailRouter>();

WebApplication app = builder.Build();

app.UseAuthentication();
app.Use(async (context, next) =>
{
    bool isApplicationDocument = context.Request.Path == "/" ||
        context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

    if (isApplicationDocument && context.User.Identity?.IsAuthenticated != true)
    {
        context.Response.Redirect("/login");
        return;
    }

    await next();
});
app.UseAuthorization();
app.UseStaticFiles();

app.MapGet("/login", (IWebHostEnvironment environment) =>
    Results.File(
        Path.Combine(environment.WebRootPath, "login.html"),
        "text/html; charset=utf-8"))
    .AllowAnonymous();

app.MapGet("/", (IWebHostEnvironment environment) =>
    Results.File(
        Path.Combine(environment.WebRootPath, "index.html"),
        "text/html; charset=utf-8"))
    .RequireAuthorization();

app.MapStudioAuthentication();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapGet("/api/email/status", (
    MicrosoftGraphEmailClient graphClient,
    MicrosoftGraphEmailOptions options,
    UnroutedEmailStore unrouted) => Results.Ok(new
{
    configured = graphClient.IsConfigured,
    collectionMailbox = options.Mailbox,
    pollSeconds = (int)graphClient.PollInterval.TotalSeconds,
    mode = graphClient.IsConfigured ? "Microsoft Graph" : "Manual/Test",
    unroutedCount = unrouted.GetOpen().Count
}));

app.MapGet("/api/workspaces", (DemoStore store) => Results.Ok(store.GetWorkspaces()));
app.MapGet("/api/conversations", (Guid? workspaceId, DemoStore store) => Results.Ok(store.GetConversations(workspaceId)));
app.MapGet("/api/conversations/{id:guid}", (Guid id, DemoStore store) =>
{
    DemoConversation? conversation = store.GetConversation(id);
    return conversation is null ? Results.NotFound() : Results.Ok(conversation);
});
app.MapPost("/api/interactions", (IncomingInteractionRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.ContactName) ||
        string.IsNullOrWhiteSpace(request.ContactAddress) ||
        string.IsNullOrWhiteSpace(request.Subject) ||
        string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "Contact, address, subject and message are required." });
    }

    return Results.Created("/api/conversations", store.ReceiveInteraction(request));
});

app.MapPost("/api/email/inbound", (
    InboundEmail email,
    IInboundEmailRouter router,
    DemoStore store,
    UnroutedEmailStore unrouted) =>
{
    bool hasRoutingEvidence = email.ToAddresses.Count > 0 ||
        email.CcAddresses.Count > 0 ||
        email.InternetHeaders.ContainsKey("X-Aegis-Route");

    if (string.IsNullOrWhiteSpace(email.MessageId) ||
        string.IsNullOrWhiteSpace(email.FromAddress) ||
        !hasRoutingEvidence ||
        string.IsNullOrWhiteSpace(email.Subject) ||
        string.IsNullOrWhiteSpace(email.Body))
    {
        return Results.BadRequest(new { error = "Message id, sender, routing evidence, subject and body are required." });
    }

    EmailRouteResult route = router.Route(email);
    if (!route.IsRouted)
    {
        UnroutedEmail reviewItem = unrouted.Capture(
            email,
            route.FailureReason ?? "No route was resolved.");

        return Results.UnprocessableEntity(new
        {
            routed = false,
            reviewId = reviewItem.Id,
            route.FailureReason,
            to = email.ToAddresses,
            cc = email.CcAddresses
        });
    }

    EmailIngestResult result = store.ReceiveEmail(email, route);
    return Results.Ok(new
    {
        routed = true,
        workspaceId = route.WorkspaceId,
        workspaceName = route.WorkspaceName,
        matchedAddress = route.MatchedAddress,
        conversationId = result.Conversation.Id,
        result.CreatedConversation,
        result.WasDuplicate
    });
});

app.MapGet("/api/email/unrouted", (UnroutedEmailStore unrouted) => Results.Ok(unrouted.GetOpen()));
app.MapGet("/api/email/unrouted/{id:guid}", (Guid id, UnroutedEmailStore unrouted) =>
{
    UnroutedEmail? item = unrouted.Get(id);
    return item is null ? Results.NotFound() : Results.Ok(item);
});
app.MapPost("/api/email/unrouted/{id:guid}/route", (
    Guid id,
    ResolveUnroutedEmailRequest request,
    UnroutedEmailStore unrouted,
    DemoStore store) =>
{
    UnroutedEmail? item = unrouted.Get(id);
    if (item is null || item.ResolvedAt.HasValue)
    {
        return Results.NotFound();
    }

    DemoWorkspace? workspace = store.GetWorkspaces().FirstOrDefault(x => x.Id == request.WorkspaceId);
    if (workspace is null)
    {
        return Results.BadRequest(new { error = "The selected workspace does not exist." });
    }

    EmailRouteResult manualRoute = new(
        true,
        workspace.Id,
        workspace.Name,
        null,
        null);
    EmailIngestResult result = store.ReceiveEmail(item.ToInboundEmail(), manualRoute);
    unrouted.MarkResolved(item.Id, workspace.Id, workspace.Name);

    return Results.Ok(new
    {
        routed = true,
        workspaceId = workspace.Id,
        workspaceName = workspace.Name,
        conversationId = result.Conversation.Id,
        result.CreatedConversation,
        result.WasDuplicate
    });
});

app.MapGet("/api/email/outbox", (OutboundEmailStore outbox) => Results.Ok(outbox.GetPending()));
app.MapPost("/api/email/outbox/{id:guid}/sent", (Guid id, MarkOutboundSentRequest request, OutboundEmailStore outbox) =>
{
    OutboundEmail? message = outbox.MarkSent(id, request.ProviderMessageId);
    return message is null ? Results.NotFound() : Results.Ok(message);
});

app.MapPost("/api/conversations/{id:guid}/messages", (
    Guid id,
    AddMessageRequest request,
    DemoStore store,
    OutboundEmailStore outbox) =>
{
    if (string.IsNullOrWhiteSpace(request.Body))
    {
        return Results.BadRequest(new { error = "Message body is required." });
    }

    DemoConversation? conversation = store.GetConversation(id);
    if (conversation is null)
    {
        return Results.NotFound();
    }

    DemoConversation updated = store.AddMessage(id, request);
    bool shouldQueueEmail = !request.IsInternal &&
        string.Equals(updated.Channel, "Email", StringComparison.OrdinalIgnoreCase);
    OutboundEmail? queuedEmail = shouldQueueEmail ? outbox.Queue(updated, request.Body) : null;

    return Results.Ok(new
    {
        conversation = updated,
        outboundEmail = queuedEmail
    });
});
app.MapPost("/api/conversations/{id:guid}/jobs", (Guid id, CreateJobRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Job title is required." });
    }

    DemoConversation? conversation = store.GetConversation(id);
    return conversation is null ? Results.NotFound() : Results.Ok(store.CreateJob(id, request));
});

app.MapGet("/api/conversations/{id:guid}/attachments", (Guid id, DemoStore store, AttachmentStore attachments) =>
{
    return store.GetConversation(id) is null
        ? Results.NotFound()
        : Results.Ok(attachments.GetForConversation(id));
});

app.MapPost("/api/conversations/{id:guid}/attachments", async (
    Guid id,
    IFormFile file,
    string? visibility,
    DemoStore store,
    AttachmentStore attachments,
    CancellationToken cancellationToken) =>
{
    if (store.GetConversation(id) is null)
    {
        return Results.NotFound();
    }

    if (file.Length == 0)
    {
        return Results.BadRequest(new { error = "The attachment is empty." });
    }

    const long maximumAttachmentSize = 100L * 1024L * 1024L;
    if (file.Length > maximumAttachmentSize)
    {
        return Results.BadRequest(new { error = "Attachments are currently limited to 100 MB." });
    }

    DemoAttachment attachment = await attachments.AddAsync(
        id,
        file,
        visibility ?? "External",
        cancellationToken);

    return Results.Created($"/api/attachments/{attachment.Id}", attachment);
}).DisableAntiforgery();

app.MapGet("/api/attachments/{id:guid}", (Guid id, AttachmentStore attachments) =>
{
    DemoAttachment? attachment = attachments.Get(id);
    if (attachment is null)
    {
        return Results.NotFound();
    }

    string path = attachments.GetPhysicalPath(attachment);
    return File.Exists(path)
        ? Results.File(path, attachment.ContentType, attachment.FileName)
        : Results.NotFound();
});

app.MapGet("/api/jobs", (DemoStore store) => Results.Ok(store.GetJobs()));
app.MapGet("/api/jobs/{id:guid}", (Guid id, DemoStore store) =>
{
    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});
app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, DemoStore store) =>
{
    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(store.UpdateJob(id, request));
});
app.MapPost("/api/jobs/{id:guid}/financial-events", (Guid id, AddFinancialEventRequest request, DemoStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Kind) || request.Amount == 0)
    {
        return Results.BadRequest(new { error = "Financial event type and non-zero amount are required." });
    }

    DemoJob? job = store.GetJob(id);
    return job is null ? Results.NotFound() : Results.Ok(store.AddFinancialEvent(id, request));
});
app.MapGet("/api/accounts/summary", (DemoStore store) => Results.Ok(store.GetAccountsSummary()));

app.MapFallbackToFile("index.html").RequireAuthorization();

app.Run();
