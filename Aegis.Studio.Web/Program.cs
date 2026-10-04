using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.WorkspaceJobs;
using Aegis.Studio.Web.Versioning;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Aegis.Pos.Core;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Domain.Bookings;
using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web;
using Aegis.Studio.Web.Authentication;
using Aegis.Studio.Web.Bookings;
using Aegis.Studio.Web.Configuration;
using Aegis.Studio.Web.Diagnostics;
using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Galleries;
using Aegis.Studio.Web.Messaging;
using Aegis.Studio.Web.Notifications;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Pos;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Profiles;
using Aegis.Studio.Web.Scheduling;
using Aegis.Studio.Web.Storage;
using Aegis.Studio.Web.WorkDay;
using Common.Diagnostics;
using Common.Secrets;
using Common.Storage;
using Common.Registration;
using Common.Security.Abstractions;
using Common.Security.Integration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string operationsLogInstanceId = builder.Configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
string operationsLogIdentityFile = builder.Configuration["Aegis:Registration:IdentityFile"]
    ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Studio", "registration-identity.json")
        : "/var/lib/aegis/studio/registration-identity.json");
builder.Services.AddAegisOperationsLogging(builder.Configuration, "Aegis.Studio", operationsLogInstanceId, operationsLogIdentityFile, builder.Environment.EnvironmentName);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR();
builder.Services.AddStudioEngineeringDiagnostics();
builder.Services.AddHostedService<Aegis.Studio.Web.OperationsTelemetryPublisher>();
builder.Services.AddSingleton<StudioOperationsFlowPublisher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StudioOperationsFlowPublisher>());

ISecretProvider secretProvider = CommonSecretProviderFactory.Create(builder.Configuration);
builder.Services.AddSingleton(secretProvider);

string storageProvider = builder.Configuration["Storage:Provider"]?.Trim() ?? "LocalFolder";

if (!builder.Environment.IsProduction() &&
    storageProvider.Equals("NetworkFolder", StringComparison.OrdinalIgnoreCase))
{
    string networkRoot = builder.Configuration["Storage:NetworkFolder:RootPath"]?.Trim() ?? "(not configured)";
    throw new InvalidOperationException(
        $"ST-CONFIG-STORAGE-ENV-001: NetworkFolder storage is production-only. " +
        $"Environment '{builder.Environment.EnvironmentName}' resolved Storage:Provider=NetworkFolder " +
        $"with root '{networkRoot}'. Remove the higher-precedence configuration override.");
}
string? storageProviderSecret = null;
string? s3AccessKeyId = null;
string? s3SecretAccessKey = null;

if (storageProvider.Equals("SharePoint", StringComparison.OrdinalIgnoreCase))
{
    string secretName = builder.Configuration["Storage:SharePoint:ClientSecretName"]?.Trim()
        ?? "storage/sharepoint/client-secret";
    storageProviderSecret = await secretProvider.GetAsync(secretName);
}
else if (storageProvider.Equals("AzureBlob", StringComparison.OrdinalIgnoreCase))
{
    string secretName = builder.Configuration["Storage:AzureBlob:ConnectionStringSecretName"]?.Trim()
        ?? "storage/azureblob/connection-string";
    storageProviderSecret = await secretProvider.GetAsync(secretName);
}
else if (storageProvider.Equals("S3", StringComparison.OrdinalIgnoreCase))
{
    string accessKeySecretName = builder.Configuration["Storage:S3:AccessKeyIdSecretName"]?.Trim()
        ?? "storage/s3/access-key-id";
    string secretKeySecretName = builder.Configuration["Storage:S3:SecretAccessKeySecretName"]?.Trim()
        ?? "storage/s3/secret-access-key";
    s3AccessKeyId = await secretProvider.GetAsync(accessKeySecretName);
    s3SecretAccessKey = await secretProvider.GetAsync(secretKeySecretName);
}
else if (storageProvider.Equals("AmazonS3", StringComparison.OrdinalIgnoreCase))
{
    string secretName = builder.Configuration["Storage:AmazonS3:SecretAccessKeyName"]?.Trim()
        ?? "storage/amazons3/secret-access-key";
    storageProviderSecret = await secretProvider.GetAsync(secretName);
}

StorageTargetOptions storageTarget = StorageTargetOptions.FromConfiguration(
    builder.Configuration,
    storageProviderSecret,
    s3AccessKeyId: s3AccessKeyId,
    s3SecretAccessKey: s3SecretAccessKey);

if (storageTarget.Kind is StorageTargetKind.LocalFolder or StorageTargetKind.NetworkFolder &&
    !Path.IsPathRooted(storageTarget.RootPath))
{
    storageTarget = storageTarget with
    {
        RootPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, storageTarget.RootPath!))
    };
}
builder.Services.AddCommonStorage(storageTarget);

builder.Services.AddCommonDiagnostics();
builder.Services.AddSingleton(new DiagnosticLevelExecutionOptions
{
    Application = "Aegis.Studio",
    Component = "Aegis.Studio.Web",
    InstanceId = operationsLogInstanceId,
    CatalogVersion = "1"
});
builder.Services.AddSingleton(new DiagnosticLevelStoreOptions
{
    Path = builder.Configuration["Aegis:Diagnostics:StorePath"]?.Trim()
        ?? Path.Combine(builder.Environment.ContentRootPath, "data", "diagnostic-level", "studio.db")
});
builder.Services.AddSingleton<IDiagnosticLevelRequestCredentialProvider, CommonSecretsDiagnosticLevelRequestCredentialProvider>();
builder.Services.AddDiagnosticLevelTest<BasicRuntimeIdentityDiagnosticLevelTest>();
builder.Services.AddDiagnosticLevelTest<BasicControlPlaneConfigurationDiagnosticLevelTest>();
builder.Services.AddDiagnosticLevelTest<CommonSecretsAvailabilityDiagnosticLevelTest>();

string studioConnectionString = await secretProvider.GetRequiredAsync("ConnectionStrings:Studio");

NpgsqlDataSource dataSource = NpgsqlDataSource.Create(studioConnectionString);
builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<StudioDatabase>();
builder.Services.AddSingleton<StudioFeatureDatabase>();
builder.Services.AddSingleton<StudioMessagingDatabase>();

MicrosoftGraphEmailOptions graphEmailOptions = builder.Configuration.GetSection("MicrosoftGraphEmail").Get<MicrosoftGraphEmailOptions>() ?? new MicrosoftGraphEmailOptions();
AuthenticationPluginLoader.Register(
    builder.Services,
    builder.Configuration,
    builder.Environment.ContentRootPath);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.Cookie.Name = "Aegis.Studio.Auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.LoginPath = "/login"; options.AccessDeniedPath = "/login"; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true; options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; } context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; }; });
builder.Services.AddAuthorization(options => { options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(graphEmailOptions);
builder.Services.AddSingleton<MicrosoftGraphEmailClient>();

global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingSecrets(builder.Services);
global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingPostgreSqlDelivery(
        builder.Services,
        studioConnectionString,
        "aegis-studio");
if (builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false))
{
    global::Common.Messaging.Hosting.ChannelServiceCollectionExtensions
        .AddMicrosoftGraphEmailMessagingChannel(
            builder.Services,
            new global::Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphEmailOptions
            {
                TenantId = builder.Configuration["Messaging:MicrosoftGraph:TenantId"] ?? string.Empty,
                ClientId = builder.Configuration["Messaging:MicrosoftGraph:ClientId"] ?? string.Empty,
                SenderUpn = builder.Configuration["Messaging:MicrosoftGraph:Email:SenderUpn"] ?? string.Empty,
                ClientSecretName = builder.Configuration["Messaging:MicrosoftGraph:ClientSecretName"]
                    ?? "messaging/msgraph/client-secret",
                SkipSenderRecipient = false
            });
}
global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingDiagnostics(builder.Services);
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<WorkspaceAccessService>();
builder.Services.AddSingleton<JobAccessService>();
builder.Services.AddSingleton<StudioStore>();
builder.Services.AddSingleton<BookingStore>();
builder.Services.AddSingleton<BookingApiAuthenticator>();
builder.Services.AddSingleton<UserProfileStore>();
builder.Services.AddSingleton<StudioInAppMessageStore>();
builder.Services.AddSingleton<StudioRecipientResolver>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<WorkspaceTimelineService>();
builder.Services.AddSingleton<JobEventNotifier>();
builder.Services.AddSingleton<ScheduleStore>();
builder.Services.AddSingleton<ProductionStore>();
builder.Services.AddSingleton<PerformanceMetricsStore>();
builder.Services.AddSingleton<WorkDayStore>();
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
builder.Services.AddHostedService<ConfigurationContractPublisher>();
builder.Services.AddSingleton<IClientGalleryPublisher,ManifestClientGalleryPublisher>();

WebApplication app = builder.Build();
app.MapDiagnosticLevelRuntime();
await app.Services.GetRequiredService<StudioDatabase>().InitializeAsync();
await app.Services.GetRequiredService<StudioFeatureDatabase>().InitializeAsync();
await app.Services.GetRequiredService<StudioMessagingDatabase>().InitializeAsync();
StudioOperationsEnhancements.EnsureSchema(
    app.Services.GetRequiredService<StudioDatabase>(),
    app.Services.GetRequiredService<WorkspaceStore>());
WorkDayStore workDayStore=app.Services.GetRequiredService<WorkDayStore>();
workDayStore.EnsureSchema();
int workDayLocationRetentionDays=Math.Clamp(app.Configuration.GetValue<int?>("WorkDay:LocationRetentionDays")??30,1,3650);
workDayStore.ApplyLocationRetention(TimeSpan.FromDays(workDayLocationRetentionDays));

app.Use(async(context,next)=>
{
    try{await next();}
    catch(Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(error,"Studio request failed: {Method} {Path}",context.Request.Method,context.Request.Path);
        context.Response.Clear();
        context.Response.StatusCode=error is ArgumentException?400:error is KeyNotFoundException?404:503;
        await context.Response.WriteAsJsonAsync(new{error=error is ArgumentException?error.Message:"This action could not be completed. Your saved data is retained; refresh and retry."});
    }
});
app.UseAuthentication();
app.UseMiddleware<WorkDayDeviceTokenMiddleware>();
app.Use(async (context, next) => { bool document = context.Request.Path == "/" || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase); if (document && context.User.Identity?.IsAuthenticated != true) { context.Response.Redirect("/login"); return; } await next(); });
app.UseAuthorization();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        string extension = Path.GetExtension(context.File.Name);
        if (extension is ".html" or ".js" or ".css")
            context.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.MapGet("/login", (IWebHostEnvironment e) => Results.File(Path.Combine(e.WebRootPath, "login.html"), "text/html; charset=utf-8")).AllowAnonymous();
app.MapGet("/", (HttpContext context,IWebHostEnvironment e) => { context.Response.Headers.CacheControl = "no-cache"; return Results.File(Path.Combine(e.WebRootPath, "index.html"), "text/html; charset=utf-8"); }).RequireAuthorization();
app.MapStudioAuthentication();
app.MapStudioUserProfiles();
app.MapStudioNotifications();
app.MapStudioInAppMessaging();
app.MapStudioMessageAttachments();
app.MapStudioOperationsEnhancements();
app.MapStudioScheduling();
app.MapStudioProduction();
app.MapWorkspaceAccess();
app.MapStudioDemoData();
new WorkspaceJobStore(app.Services.GetRequiredService<StudioDatabase>()).EnsureSchema();
StudioVersionSchema.EnsureCreated(app.Services.GetRequiredService<StudioDatabase>());
app.MapWorkspaceJobs();
app.MapMediaProduction();
app.MapJobLifecycle();
new FrontDeskBookingRequestStore(app.Services.GetRequiredService<StudioDatabase>()).EnsureSchema();
app.MapFrontDeskBookingRequests();
app.MapStudioClients();
app.MapStudioQuotes();
app.MapJobAccess();
app.MapAegisHealth(
    "Aegis.Studio",
    async (services, ct) =>
    {
        var report = await services
            .GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>()
            .CheckHealthAsync(ct);
        return report.Status switch
        {
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy =>
                AegisHealthAssessment.Healthy("Studio health checks are healthy."),
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded =>
                AegisHealthAssessment.Degraded("Studio health checks are degraded."),
            _ => AegisHealthAssessment.Unhealthy("Studio health checks are unhealthy.")
        };
    })
    .AllowAnonymous();
app.MapStudioEngineeringDiagnostics();
app.MapStudioPosHardening();
app.MapStudioGalleries();

app.MapGet("/api/email/status", async (MicrosoftGraphEmailClient client, MicrosoftGraphEmailOptions options, UnroutedEmailStore unrouted, CancellationToken cancellationToken) =>
{
    string? slackWebhook = await secretProvider.GetAsync("Notifications:SlackWebhookUrl", cancellationToken);
    string? teamsWebhook = await secretProvider.GetAsync("Notifications:TeamsWebhookUrl", cancellationToken);
    return Results.Ok(new
    {
        enabled = options.Enabled,
        configured = client.IsConfigured,
        collectionMailbox = options.Mailbox,
        pollSeconds = (int)client.PollInterval.TotalSeconds,
        mode = client.IsConfigured ? "Microsoft Graph" : "Microsoft Graph - waiting for client secret",
        slackConfigured = !string.IsNullOrWhiteSpace(slackWebhook),
        teamsConfigured = !string.IsNullOrWhiteSpace(teamsWebhook),
        unroutedCount = unrouted.GetOpen().Count
    });
});
app.MapGet("/api/booking-security/status", (BookingApiAuthenticator authentication) => Results.Ok(authentication.GetStatus())).RequireAuthorization();

app.MapGet("/api/workspaces", (ClaimsPrincipal user, StudioStore store, WorkspaceAccessService access) =>
{
    HashSet<Guid> allowed = access.GetAccessibleWorkspaceIds(user).ToHashSet();
    return Results.Ok(store.GetWorkspaces().Where(x => allowed.Contains(x.Id)));
});
app.MapGet("/api/workspaces/{id:guid}/timeline", (Guid id, int? count, ClaimsPrincipal user, WorkspaceAccessService access, WorkspaceTimelineService timeline) =>
{
    if (!access.HasPermission(user, id, "View")) return Results.Forbid();
    return Results.Ok(timeline.GetForWorkspace(id, count ?? 200));
});
RouteGroupBuilder workspaceAdmin = app.MapGroup("/api/admin/workspaces").RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
workspaceAdmin.MapGet("/", (WorkspaceStore store) => Results.Ok(store.GetAll(true)));
workspaceAdmin.MapPost("/", (SaveWorkspaceRequest request, WorkspaceStore store) => string.IsNullOrWhiteSpace(request.Name) ? Results.BadRequest(new { error = "Workspace name is required." }) : Results.Created("/api/admin/workspaces", store.Create(request)));
workspaceAdmin.MapPut("/{id:guid}", (Guid id, SaveWorkspaceRequest request, WorkspaceStore store) => { try { return Results.Ok(store.Update(id, request)); } catch (KeyNotFoundException) { return Results.NotFound(); } });

app.MapGet("/api/conversations", (Guid? workspaceId, ClaimsPrincipal user, StudioStore store, WorkspaceAccessService access) =>
{
    if (workspaceId.HasValue && !access.HasPermission(user, workspaceId.Value, "View")) return Results.Forbid();
    HashSet<Guid> allowed = access.GetAccessibleWorkspaceIds(user).ToHashSet();
    return Results.Ok(store.GetConversations(workspaceId).Where(x => allowed.Contains(x.WorkspaceId)));
});
app.MapGet("/api/conversations/{id:guid}", (Guid id, ClaimsPrincipal user, StudioStore store, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess) =>
{
    StudioConversation? value = store.GetConversation(id);
    if (value is null) return Results.NotFound();
    bool allowed = workspaceAccess.HasPermission(user, value.WorkspaceId, "View") || (value.JobId.HasValue && jobAccess.HasPermission(user, value.JobId.Value, "ViewConversation"));
    return allowed ? Results.Ok(value) : Results.Forbid();
});
app.MapPost("/api/interactions", (IncomingInteractionRequest request, ClaimsPrincipal user, StudioStore store, NotificationService notifications, WorkspaceAccessService access) =>
{
    if (request.WorkspaceId.HasValue && !access.HasPermission(user, request.WorkspaceId.Value, "ReplyRequester")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.ContactName) || string.IsNullOrWhiteSpace(request.ContactAddress) || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Contact, address, subject and message are required." });
    try { StudioConversation conversation = store.ReceiveInteraction(request); notifications.PublishToAll("WorkspaceMessage", $"New request: {conversation.Subject}", $"{conversation.ContactName} sent a new {conversation.Channel} request.", "Conversation", conversation.Id.ToString("D")); return Results.Created("/api/conversations", conversation); }
    catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/email/inbound", (InboundEmail email, IInboundEmailRouter router, StudioStore store, UnroutedEmailStore unrouted, NotificationService notifications, StudioOperationsFlowPublisher operationsFlows) =>
{
    bool evidence = email.ToAddresses.Count > 0 || email.CcAddresses.Count > 0 || email.InternetHeaders.ContainsKey("X-Aegis-Route");
    if (string.IsNullOrWhiteSpace(email.MessageId) || string.IsNullOrWhiteSpace(email.FromAddress) || !evidence || string.IsNullOrWhiteSpace(email.Subject) || string.IsNullOrWhiteSpace(email.Body)) return Results.BadRequest(new { error = "Message id, sender, routing evidence, subject and body are required." });
    EmailRouteResult route = router.Route(email);
    string correlationId = email.MessageId;
    if (!route.IsRouted)
    {
        UnroutedEmail item = unrouted.Capture(email, route.FailureReason ?? "No route was resolved.");
        operationsFlows.TryEnqueueInbound(correlationId, "Router", businessId: item.Id.ToString("D"), detail: route.FailureReason, failed: true);
        return Results.UnprocessableEntity(new { routed = false, reviewId = item.Id, route.FailureReason });
    }
    operationsFlows.TryEnqueueInbound(correlationId, "Router", businessId: route.WorkspaceId?.ToString("D"));
    EmailIngestResult result = store.ReceiveEmail(email, route);
    operationsFlows.TryEnqueueInbound(correlationId, "Workspace", businessId: result.Conversation.Id.ToString("D"));
    operationsFlows.TryEnqueueInbound(correlationId, "Conversation", businessId: result.Conversation.Id.ToString("D"));
    if (!result.WasDuplicate)
    {
        notifications.PublishToAll("WorkspaceMessage", $"Email: {email.Subject}", $"New email from {email.FromName} routed to {route.WorkspaceName}.", "Conversation", result.Conversation.Id.ToString("D"));
        operationsFlows.TryEnqueueInbound(correlationId, "Notification", businessId: result.Conversation.Id.ToString("D"));
    }
    return Results.Ok(new { routed = true, workspaceId = route.WorkspaceId, conversationId = result.Conversation.Id, result.CreatedConversation, result.WasDuplicate });
});
app.MapGet("/api/email/unrouted", (UnroutedEmailStore store) => Results.Ok(store.GetOpen()));
app.MapGet("/api/email/outbox", (OutboundEmailStore store) => Results.Ok(store.GetPending()));
app.MapGet("/api/email/audit", (int? count, ClaimsPrincipal user, OutboundEmailStore store, WorkspaceAccessService access) =>
{
    IReadOnlyList<Guid> workspaceIds = access.GetAccessibleWorkspaceIds(user);
    return Results.Ok(store.GetRecentAudit(workspaceIds, count ?? 200));
}).RequireAuthorization();
app.MapGet("/api/conversations/{id:guid}/sending-identities", (Guid id, ClaimsPrincipal user, StudioStore store, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess, WorkspaceStore workspaces, UserProfileStore profiles, IConfiguration configuration) =>
{
    StudioConversation? conversation = store.GetConversation(id);
    if (conversation is null) return Results.NotFound();

    bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "View") ||
        (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "ViewConversation"));
    if (!allowed) return Results.Forbid();

    StudioWorkspaceDefinition workspace = workspaces.Get(conversation.WorkspaceId)
        ?? throw new InvalidOperationException("The conversation workspace no longer exists.");

    var identities = new List<object>();
    if (!string.IsNullOrWhiteSpace(workspace.EmailAddress))
    {
        identities.Add(new
        {
            type = "Workspace",
            label = $"Workspace — {workspace.EmailAddress}",
            fromAddress = workspace.EmailAddress,
            isDefault = true
        });
    }

    StudioUserProfile profile = profiles.GetOrCreate(user);
    bool canSendAsSelf = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "SendAsSelf");
    string[] allowedDomains = configuration.GetSection("Messaging:AllowedPersonalSenderDomains").Get<string[]>() ?? [];

    if (canSendAsSelf && !string.IsNullOrWhiteSpace(profile.Email))
    {
        try
        {
            StudioSendingIdentity self = StudioSendingIdentityResolver.Resolve(
                "Self",
                workspace.EmailAddress ?? string.Empty,
                profile.UserName,
                profile.Email,
                true,
                allowedDomains);

            identities.Add(new
            {
                type = "Self",
                label = $"Myself — {self.FromAddress}",
                fromAddress = self.FromAddress,
                isDefault = false
            });
        }
        catch (InvalidOperationException)
        {
            // Fail closed: invalid/unapproved personal identities are simply not offered.
        }
    }

    return Results.Ok(new
    {
        defaultIdentity = "Workspace",
        canSendAsSelf,
        identities
    });
}).RequireAuthorization();

app.MapPost("/api/conversations/{id:guid}/messages", (Guid id, AddMessageRequest request, ClaimsPrincipal user, StudioStore store, OutboundEmailStore outbox, NotificationService notifications, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess, WorkspaceStore workspaces, UserProfileStore profiles, IConfiguration configuration, StudioOperationsFlowPublisher operationsFlows) =>
{
    if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Message body is required." });
    StudioConversation? current = store.GetConversation(id); if (current is null) return Results.NotFound();
    bool allowed = workspaceAccess.HasPermission(user, current.WorkspaceId, "ReplyRequester") || (current.JobId.HasValue && jobAccess.HasPermission(user, current.JobId.Value, "ReplyRequester"));
    if (!allowed) return Results.Forbid();
    StudioSendingIdentity? sendingIdentity = null;
    bool externalEmailReply = !request.IsInternal && string.Equals(current.Channel, "Email", StringComparison.OrdinalIgnoreCase);
    if (externalEmailReply)
    {
        operationsFlows.TryEnqueueOutbound(id.ToString("D"), "User", businessId: id.ToString("D"));
        StudioWorkspaceDefinition workspace = workspaces.Get(current.WorkspaceId)
            ?? throw new InvalidOperationException("The conversation workspace no longer exists.");
        StudioUserProfile profile = profiles.GetOrCreate(user);
        bool canSendAsSelf = workspaceAccess.HasPermission(user, current.WorkspaceId, "SendAsSelf");
        string[] allowedDomains = configuration
            .GetSection("Messaging:AllowedPersonalSenderDomains")
            .Get<string[]>() ?? [];

        try
        {
            sendingIdentity = StudioSendingIdentityResolver.Resolve(
                request.SendingIdentity,
                workspace.EmailAddress ?? string.Empty,
                profile.UserName,
                profile.Email,
                canSendAsSelf,
                allowedDomains);
            operationsFlows.TryEnqueueOutbound(
                id.ToString("D"),
                "SendingIdentity",
                businessId: id.ToString("D"));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    StudioConversation updated = store.AddMessage(id, request, user);
    OutboundEmail? email = externalEmailReply
        ? outbox.Queue(updated, request.Body, sendingIdentity)
        : null;
    string correlationId = id.ToString("D");
    if (externalEmailReply)
        operationsFlows.TryEnqueueOutbound(correlationId, "Message", businessId: email?.Id.ToString("D"));
    if (request.IsInternal) notifications.PublishToAll("Mention", $"Internal note: {updated.Subject}", request.Body, "Conversation", id.ToString("D"));
    string messageAuthor = user.FindFirstValue("login_name") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "unknown";
    StudioMessage? createdMessage = updated.Messages
        .Where(x => string.Equals(x.AuthorUserName, messageAuthor, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Body, request.Body.Trim(), StringComparison.Ordinal))
        .OrderByDescending(x => x.CreatedAt)
        .FirstOrDefault();
    return Results.Ok(new
    {
        conversation = updated,
        messageId = createdMessage?.Id,
        outboundEmail = email,
        sendingIdentity = email is null ? null : new
        {
            type = email.SendingIdentityType,
            fromAddress = email.FromAddress,
            actualUser = email.SentByUserName
        }
    });
});
app.MapPost("/api/conversations/{id:guid}/jobs", (Guid id, CreateJobRequest request, ClaimsPrincipal user, StudioStore store, WorkspaceAccessService access, StudioOperationsFlowPublisher flows, StudioDatabase database) =>
{
    StudioConversation? conversation = store.GetConversation(id);
    if (conversation is null) return Results.NotFound();
    if (!access.HasPermission(user, conversation.WorkspaceId, "CreateJob")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Title)) return Results.BadRequest(new { error = "Job title is required." });
    if(new FrontDeskBookingRequestStore(database).HasPendingConversation(id)) return Results.Conflict(new { error = "This enquiry has a pending FrontDesk booking request. Record its required deposit through FrontDesk to create the job." });
    StudioJob job=store.CreateJob(id, request);
    flows.TryEnqueueJobFlow(job.Id.ToString("D"),"Created",job.Id.ToString("D"),$"Job created in workspace {job.WorkspaceId:D}.");
    return Results.Ok(job);
});
app.MapGet("/api/jobs", (ClaimsPrincipal user, StudioStore store, JobAccessService access) =>
{
    HashSet<Guid> allowed = access.GetAccessibleJobIds(user).ToHashSet();
    return Results.Ok(store.GetJobs().Where(x => allowed.Contains(x.Id)));
});
app.MapGet("/api/jobs/{id:guid}", (Guid id, ClaimsPrincipal user, StudioStore store, JobAccessService access) =>
{
    if (!access.HasPermission(user, id, "ViewJob")) return Results.Forbid();
    return store.GetJob(id) is StudioJob job ? Results.Ok(job) : Results.NotFound();
});
app.MapGet("/api/jobs/{id:guid}/timeline", (Guid id, int? count, ClaimsPrincipal user, StudioStore store, JobAccessService access, WorkspaceTimelineService timeline) =>
{
    if (store.GetJob(id) is null) return Results.NotFound();
    if (!access.HasPermission(user, id, "ViewJob")) return Results.Forbid();
    return Results.Ok(timeline.GetForJob(id, count ?? 200));
});
app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, ClaimsPrincipal user, StudioStore store, NotificationService notifications, JobEventNotifier jobEvents, JobAccessService access, StudioOperationsFlowPublisher flows) =>
{
    StudioJob? before = store.GetJob(id); if (before is null) return Results.NotFound();
    if (!access.HasPermission(user, id, "EditJob")) return Results.Forbid();
    if (!string.IsNullOrWhiteSpace(request.Status) && !string.Equals(before.Status, request.Status, StringComparison.OrdinalIgnoreCase) && !access.HasPermission(user, id, "ChangeStatus")) return Results.Forbid();
    if (!string.IsNullOrWhiteSpace(request.AssignedTo) && !string.Equals(before.AssignedTo, request.AssignedTo, StringComparison.OrdinalIgnoreCase) && !access.HasPermission(user, id, "AssignStaff")) return Results.Forbid();
    StudioJob updated = store.UpdateJob(id, request);
    if (!string.Equals(before.Status, updated.Status, StringComparison.OrdinalIgnoreCase))
    {
        jobEvents.Publish(id, "JobStatusChanged", $"Job status: {updated.Title}", $"{before.Status} → {updated.Status}", requesterEligible: true);
        string? flowStage=updated.Status.Trim() switch
        {
            "Accepted"=>"Accepted",
            "Arrived"=>"Arrived",
            "InProgress"=>"InProgress",
            "Completed"=>"Completed",
            _=>null
        };
        if(flowStage is not null) flows.TryEnqueueJobFlow(id.ToString("D"),flowStage,id.ToString("D"),$"Job status changed to {updated.Status}.");
    }
    if (!string.Equals(before.AssignedTo, updated.AssignedTo, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(updated.AssignedTo))
    {
        access.EnsureAssignedMember(id, updated.AssignedTo);
        notifications.PublishToUser(updated.AssignedTo, "JobAssigned", $"Job assigned: {updated.Title}", $"You were assigned to {updated.Title} for {updated.ClientName}.", "Job", id.ToString("D"));
        flows.TryEnqueueJobFlow(id.ToString("D"),"Assigned",id.ToString("D"),$"Assigned to {updated.AssignedTo}.");
    }
    return Results.Ok(updated);
});


app.MapPost("/api/jobs/{id:guid}/pipeline-transition", (Guid id, PipelineJobTransitionRequest request, ClaimsPrincipal user, StudioStore store, JobAccessService access, WorkspaceAccessService workspaceAccess, StudioDatabase database, StudioInAppMessageStore messages, JobEventNotifier jobEvents, StudioOperationsFlowPublisher flows) =>
{
    StudioJob? before=store.GetJob(id);if(before is null)return Results.NotFound();
    if(!access.HasPermission(user,id,"EditJob"))return Results.Forbid();
    bool statusChanged=!string.IsNullOrWhiteSpace(request.Status)&&!string.Equals(before.Status,request.Status,StringComparison.OrdinalIgnoreCase);
    bool workspaceChanged=request.WorkspaceId.HasValue&&request.WorkspaceId.Value!=before.WorkspaceId;
    if(statusChanged&&!access.HasPermission(user,id,"ChangeStatus"))return Results.Forbid();
    if(workspaceChanged&&!workspaceAccess.HasPermission(user,request.WorkspaceId!.Value,"CreateJob"))return Results.Forbid();
    if(!statusChanged&&!workspaceChanged)return Results.Ok(before);
    StudioJob updated=before;
    if(statusChanged)updated=store.UpdateJob(id,new UpdateJobRequest(request.Status,before.AssignedTo,before.EventDate));
    if(workspaceChanged)updated=store.MoveJobWorkspace(id,request.WorkspaceId!.Value);
    string actor=user.FindFirstValue("login_name")??user.Identity?.Name??"Studio";
    string oldWorkspaceName=store.GetWorkspaces().FirstOrDefault(x=>x.Id==before.WorkspaceId)?.Name??"previous workspace";
    string newWorkspaceName=store.GetWorkspaces().FirstOrDefault(x=>x.Id==updated.WorkspaceId)?.Name??"new workspace";
    List<string> changes=[];
    if(statusChanged)changes.Add($"stage/status {before.Status} → {updated.Status}");
    if(workspaceChanged)changes.Add($"workspace {oldWorkspaceName} → {newWorkspaceName}");
    string detail=$"Pipeline change for {updated.Title}: {string.Join("; ",changes)}{(string.IsNullOrWhiteSpace(request.Reason)?string.Empty:". "+request.Reason.Trim())}";
    using(NpgsqlConnection connection=database.OpenConnection())
    using(NpgsqlCommand audit=connection.CreateCommand())
    {
        audit.CommandText="insert into studio_operational_audit(id,job_id,action,user_name,detail,created_at_utc) values($1,$2,'PipelineChanged',$3,$4,now())";
        audit.Parameters.AddWithValue(Guid.NewGuid());audit.Parameters.AddWithValue(id);audit.Parameters.AddWithValue(actor);audit.Parameters.AddWithValue(detail);audit.ExecuteNonQuery();
    }
    foreach(Guid workspaceId in new[]{before.WorkspaceId,updated.WorkspaceId}.Distinct())
    {
        try
        {
            StudioMessageThread thread=messages.GetOrCreateScoped(user,"Workspace",workspaceId);
            messages.AddMessage(thread.Id,user,detail);
        }
        catch(UnauthorizedAccessException){ }
    }
    if(statusChanged)
    {
        jobEvents.Publish(id,"JobStatusChanged",$"Job status: {updated.Title}",$"{before.Status} → {updated.Status}",requesterEligible:true);
        flows.TryEnqueueJobFlow(id.ToString("D"),"InProgress",id.ToString("D"),detail);
    }
    if(workspaceChanged)flows.TryEnqueueJobFlow(id.ToString("D"),"Workspace",id.ToString("D"),detail);
    return Results.Ok(updated);
});

app.MapGet("/api/bookings", (BookingStore store) => Results.Ok(store.GetAll()));
app.MapGet("/api/bookings/{id:guid}", (Guid id, HttpContext context, BookingStore store, BookingApiAuthenticator security) => { if (!security.IsAuthorized(context)) return Results.Unauthorized(); return store.Get(id) is Booking booking ? Results.Ok(new { booking, payments = store.GetPayments(id) }) : Results.NotFound(); }).AllowAnonymous();
// Website intake joins the same pending booking/deposit flow as FrontDesk.
app.MapPost("/api/bookings", PublicBookingEndpoints.Create).AllowAnonymous();
app.MapPost("/api/public/booking-requests", PublicBookingEndpoints.Create).AllowAnonymous();
app.MapPost("/api/public/booking-requests/{id:guid}/payment-confirmations", PublicBookingEndpoints.RecordDeposit).AllowAnonymous();
app.MapGet("/api/public/booking-requests/{id:guid}", (Guid id,HttpContext context,BookingApiAuthenticator security,StudioDatabase database)=>
{
    if(!security.IsAuthorized(context))return Results.Unauthorized();
    var pending=new FrontDeskBookingRequestStore(database).GetAll().FirstOrDefault(x=>x.Id==id);
    return pending is null?Results.NotFound():Results.Ok(new{pending.Id,pending.Status,pending.JobId,pending.RecordedDeposit,pending.Request.RequiredDeposit});
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

app.MapGet("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, ClaimsPrincipal user, StudioStore studio, StudioPosStore pos, JobAccessService access) =>
{
    if (studio.GetJob(jobId) is null) return Results.NotFound();
    if (!access.HasPermission(user, jobId, "ViewPosTransactions")) return Results.Forbid();
    return Results.Ok(pos.GetForJob(jobId));
});
app.MapPost("/api/jobs/{jobId:guid}/pos/sales", (Guid jobId, CreateStudioSaleRequest request, ClaimsPrincipal user, HttpContext context, StudioStore studio, StudioPosStore pos, JobAccessService access) =>
{
    if (studio.GetJob(jobId) is null) return Results.NotFound();
    if (!access.HasPermission(user, jobId, "UsePos")) return Results.Forbid();
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.UnitPrice < 0m || x.Quantity <= 0)) return Results.BadRequest(new { error = "At least one valid sale line is required." });
    try { Sale sale = pos.Create(jobId, request, studio, context.User.Identity?.Name ?? "Studio"); return Results.Created($"/api/pos/sales/{sale.Id}", sale); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/pos/sales/{saleId:guid}", (Guid saleId, StudioPosStore pos) => pos.Get(saleId) is Sale sale ? Results.Ok(sale) : Results.NotFound());
app.MapPost("/api/pos/sales/{saleId:guid}/complete", (Guid saleId, CompleteStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos) => { if (pos.Get(saleId) is null) return Results.NotFound(); string userId = context.User.Identity?.Name ?? "Studio"; try { return Results.Ok(pos.Complete(saleId, request, studio, userId)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); } });
app.MapPost("/api/pos/sales/{saleId:guid}/void", (Guid saleId, VoidStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos, ClaimsPrincipal user, JobAccessService access) =>
{
    Sale? sale = pos.Get(saleId); if (sale is null) return Results.NotFound();
    if (!Guid.TryParse(sale.ReferenceId, out Guid jobId) || !access.HasPermission(user, jobId, "RefundVoidPos")) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.Reason)) return Results.BadRequest(new { error = "Void reason is required." });
    try { return Results.Ok(pos.Void(saleId, context.User.Identity?.Name ?? "Studio", request.Reason)); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/pos/sales/{saleId:guid}/refunds", (Guid saleId, RefundStudioSaleRequest request, HttpContext context, StudioStore studio, StudioPosStore pos, ClaimsPrincipal user, JobAccessService access) =>
{
    Sale? sale = pos.Get(saleId); if (sale is null) return Results.NotFound();
    if (!Guid.TryParse(sale.ReferenceId, out Guid jobId) || !access.HasPermission(user, jobId, "RefundVoidPos")) return Results.Forbid();
    try { return Results.Ok(pos.Refund(saleId, request, studio, context.User.Identity?.Name ?? "Studio")); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/api/pos/device-agent", (IConfiguration configuration) => Results.Ok(new { baseUrl = configuration["PosDeviceAgent:BaseUrl"] ?? "https://localhost:7399" }));

app.MapGet("/api/conversations/{id:guid}/attachments", (Guid id, ClaimsPrincipal user, StudioStore store, AttachmentStore attachments, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess) =>
{
    StudioConversation? conversation = store.GetConversation(id); if (conversation is null) return Results.NotFound();
    bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "ViewAttachments") || (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "ViewAttachments"));
    return allowed ? Results.Ok(attachments.GetForConversation(id)) : Results.Forbid();
});
app.MapPost("/api/conversations/{id:guid}/attachments", async (Guid id, IFormFile file, string? visibility, ClaimsPrincipal user, StudioStore store, AttachmentStore attachments, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess, StudioOperationsFlowPublisher operationsFlows, CancellationToken token) =>
{
    StudioConversation? conversation = store.GetConversation(id); if (conversation is null) return Results.NotFound();
    bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "AddAttachments") || (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "AddAttachments"));
    if (!allowed) return Results.Forbid();
    try
    {
        string correlationId = id.ToString("D");
        operationsFlows.TryEnqueueAttachment(correlationId, "Received", businessId: id.ToString("D"));
        StudioAttachment attachment = await attachments.AddAsync(
            id,
            file,
            visibility ?? "External",
            user.Identity?.Name ?? "Unknown",
            token,
            correlationId);
        operationsFlows.TryEnqueueAttachment(correlationId, "Storage", businessId: attachment.Id.ToString("D"));
        operationsFlows.TryEnqueueAttachment(correlationId, "Metadata", businessId: attachment.Id.ToString("D"));
        operationsFlows.TryEnqueueAttachment(correlationId, "Workspace", businessId: attachment.Id.ToString("D"));
        return Results.Created($"/api/attachments/{attachment.Id}", attachment);
    }
    catch (Exception exception) when (exception is InvalidOperationException or Common.Storage.StorageException)
    {
        operationsFlows.TryEnqueueAttachment(id.ToString("D"), "Storage", businessId: id.ToString("D"), detail: $"Attachment persistence failed ({exception.GetType().Name}).", failed: true);
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery();
app.MapGet("/api/attachments/{id:guid}", (Guid id, ClaimsPrincipal user, AttachmentStore attachments, StudioStore store, WorkspaceAccessService workspaceAccess, JobAccessService jobAccess) =>
{
    StudioAttachment? attachment = attachments.Get(id); if (attachment is null) return Results.NotFound();
    StudioConversation? conversation = store.GetConversation(attachment.ConversationId); if (conversation is null) return Results.NotFound();
    bool allowed = workspaceAccess.HasPermission(user, conversation.WorkspaceId, "ViewAttachments") || (conversation.JobId.HasValue && jobAccess.HasPermission(user, conversation.JobId.Value, "ViewAttachments"));
    if (!allowed) return Results.Forbid();
    string path = attachments.GetPhysicalPath(attachment);
    return File.Exists(path) ? Results.File(path, attachment.ContentType, attachment.FileName, enableRangeProcessing: true) : Results.NotFound();
});

app.MapGet("/api/accounts/summary", (StudioStore store) => Results.Ok(store.GetAccountsSummary()));
app.MapFallbackToFile("index.html").RequireAuthorization();
app.Run();
