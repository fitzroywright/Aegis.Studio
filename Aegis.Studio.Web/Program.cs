using Aegis.Studio.Web.Workflow;
using Aegis.Studio.Web.Media;
using Aegis.Studio.Web.FrontDesk;
using Aegis.Studio.Web.WorkspaceJobs;
using Aegis.Studio.Web.Versioning;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Aegis.Pos.Core;
using Aegis.Studio.Application.EmailRouting;
using Aegis.Studio.Domain.Bookings;
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
using Microsoft.AspNetCore.HttpOverrides;
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

string storageProvider = builder.Configuration["Storage:Provider"]?.Trim()
    ?? throw new InvalidOperationException("Storage:Provider must be configured.");
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

builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    NpgsqlDataSourceBuilder source = new(studioConnectionString);
    source.UseLoggerFactory(services.GetRequiredService<ILoggerFactory>());
    return source.Build();
});
builder.Services.AddSingleton(services =>
    new StudioDatabase(services.GetRequiredService<NpgsqlDataSource>(), studioConnectionString));
builder.Services.AddSingleton<StudioFeatureDatabase>();
builder.Services.AddSingleton<StudioMessagingDatabase>();

AuthenticationPluginLoader.Register(
    builder.Services,
    builder.Configuration,
    builder.Environment.ContentRootPath);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.Cookie.Name = "Aegis.Studio.Auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.LoginPath = "/login"; options.AccessDeniedPath = "/login"; options.ExpireTimeSpan = TimeSpan.FromHours(8); options.SlidingExpiration = true; options.Events.OnRedirectToLogin = context => { if (context.Request.Path.StartsWithSegments("/api")) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; } context.Response.Redirect(context.RedirectUri); return Task.CompletedTask; }; });
builder.Services.AddAuthorization(options => { options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(); });
builder.Services.AddHealthChecks();

global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingSecrets(builder.Services);
global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingPostgreSqlDelivery(
        builder.Services,
        studioConnectionString,
        "aegis-studio");
bool graphEmailEnabled = builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
bool graphInboundEnabled = builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:InboundEnabled", false);
global::Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphEmailOptions graphOptions = new()
{
    TenantId = builder.Configuration["Messaging:MicrosoftGraph:TenantId"] ?? string.Empty,
    ClientId = builder.Configuration["Messaging:MicrosoftGraph:ClientId"] ?? string.Empty,
    SenderUpn = builder.Configuration["Messaging:MicrosoftGraph:Email:SenderUpn"] ?? string.Empty,
    ClientSecretName = builder.Configuration["Messaging:MicrosoftGraph:ClientSecretName"]
        ?? "messaging/msgraph/client-secret",
    SkipSenderRecipient = false,
    InboundEnabled = graphInboundEnabled,
    InboundPollSeconds = builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:InboundPollSeconds", 30),
    InboundPageSize = builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:InboundPageSize", 50)
};
global::Common.Messaging.Hosting.ChannelServiceCollectionExtensions
    .AddMicrosoftGraphMailbox(builder.Services, graphOptions);
if (graphEmailEnabled)
{
    global::Common.Messaging.Hosting.ChannelServiceCollectionExtensions
        .AddMicrosoftGraphEmailMessagingChannel(builder.Services, graphOptions);
}
if(builder.Configuration.GetValue("Messaging:WhatsApp:Enabled",false))
{
    global::Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddWhatsAppMessagingChannel(builder.Services,new global::Common.Messaging.Channels.WhatsApp.WhatsAppMessageOptions
    {
        Enabled=true,GraphApiVersion=builder.Configuration["Messaging:WhatsApp:GraphApiVersion"]??"v23.0",
        PhoneNumberId=builder.Configuration["Messaging:WhatsApp:PhoneNumberId"]??string.Empty,
        AccessTokenSecretName=builder.Configuration["Messaging:WhatsApp:AccessTokenSecretName"]??"messaging/whatsapp/access-token"
    });
}
global::Common.Messaging.Hosting.ServiceCollectionExtensions
    .AddCommonMessagingDiagnostics(builder.Services);
builder.Services.AddSingleton<WorkspaceStore>();
builder.Services.AddSingleton<WorkspaceAccessService>();
builder.Services.AddSingleton<JobAccessService>();
builder.Services.AddSingleton<StudioStore>();
builder.Services.AddSingleton<DemoSeedCoordinator>();
builder.Services.AddSingleton<BookingStore>();
builder.Services.AddSingleton<BookingApiAuthenticator>();
builder.Services.AddSingleton<UserProfileStore>();
builder.Services.AddSingleton<StudioInAppMessageStore>();
builder.Services.AddSingleton<StudioEmployeeDirectoryStore>();
builder.Services.AddSingleton<StudioMessageAttachmentStore>();
builder.Services.AddSingleton<StudioAddressBook>();
builder.Services.AddSingleton<StudioDeliveryPlanner>();
builder.Services.AddSingleton<StudioUnifiedMessaging>();
builder.Services.AddSingleton<StudioUnifiedInbox>();
builder.Services.AddSingleton<StudioUnifiedInbound>();
builder.Services.AddSingleton<StudioEmailRouting>();
builder.Services.AddHttpClient<StudioGraphSentMessageVerifier>();
builder.Services.AddSingleton<StudioEmailIntake>();
builder.Services.AddSingleton<StudioInquiryAssociations>();
builder.Services.AddHttpClient<global::Common.Messaging.IEmailRecipientDirectory,global::Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphRecipientDirectory>();
builder.Services.AddSingleton<StudioRecipientResolver>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<Aegis.Studio.Web.Reminders.ClientReminderStore>();
builder.Services.AddHostedService<Aegis.Studio.Web.Reminders.ClientReminderWorker>();
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
builder.Services.AddHostedService<Aegis.Studio.Web.Intake.WebsiteIntakeWorker>();
builder.Services.AddHostedService<Aegis.Studio.Web.Intake.GalleryPushWorker>();
builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.AddSingleton<WorkflowStore>();
builder.Services.AddHostedService<Aegis.Studio.Web.PublicApi.PublicWebhookWorker>();
builder.Services.AddHostedService<WorkflowMonitorWorker>();
builder.Services.AddHostedService<ConfigurationContractPublisher>();
builder.Services.AddSingleton<IClientGalleryPublisher,ManifestClientGalleryPublisher>();
builder.Services.AddHostedService<Aegis.Studio.Web.Media.MediaDerivativeWorker>();
builder.Services.AddSingleton<Aegis.Studio.Web.WorkDay.IWorkDaySchedulingService>(sp=>new Aegis.Studio.Web.WorkDay.WorkDaySchedulingService(sp.GetRequiredService<StudioDatabase>(),new PhotographerStore(sp.GetRequiredService<StudioDatabase>())));

WebApplication app = builder.Build();
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost
};
// Studio is bound to loopback in production and Nginx is the trusted local reverse proxy.
// Clear the framework defaults so the loopback proxy's forwarded headers are honored.
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);
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
    try{
        await next();
        if(context.Request.Path.StartsWithSegments("/api/public/v1")&&context.Response.StatusCode==404&&!context.Response.HasStarted)
            await context.Response.WriteAsJsonAsync(new Aegis.Studio.Web.PublicApi.PublicV1Error("not_found","Unknown V1 resource.",context.TraceIdentifier));
    }
    catch(Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogError(error,"Studio request failed: {Method} {Path}",context.Request.Method,context.Request.Path);
        context.Response.Clear();
        context.Response.StatusCode=error is ArgumentException?400:error is KeyNotFoundException?404:503;
        if(error is Npgsql.PostgresException {SqlState:"55000"}) context.Response.StatusCode=409;
        if(context.Request.Path.StartsWithSegments("/api/public/v1"))
            await context.Response.WriteAsJsonAsync(new Aegis.Studio.Web.PublicApi.PublicV1Error("temporarily_unavailable","The action could not be completed. Retry with the same idempotency key.",context.TraceIdentifier));
        else await context.Response.WriteAsJsonAsync(new{error=error is ArgumentException?error.Message:error is Npgsql.PostgresException {SqlState:"55000"}?"This Job is closed. Obtain Operations Manager reopening before editing operational records.":"This action could not be completed. Your saved data is retained; refresh and retry."});
    }
});
app.Use(async (context, next) =>
{
    bool status = context.Request.Path.StartsWithSegments("/api/admin/demo")
        || context.Request.Path.StartsWithSegments("/api/admin/connected-workflow/demo-data")
        || context.Request.Path.StartsWithSegments("/api/auth")
        || !context.Request.Path.StartsWithSegments("/api");
    if (status) { await next(); return; }
    using var operation = DemoMaintenance.TryEnterOperation(context.RequestServices.GetRequiredService<StudioDatabase>());
    if (operation is null)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "2";
        await context.Response.WriteAsJsonAsync(new { error = "Studio data is being cleared and recreated. Please wait for completion." });
        return;
    }
    await next();
});
app.UseMiddleware<StorageFailureMiddleware>();
app.UseMiddleware<TodayStartupTimingMiddleware>();
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
app.MapStudioToday();
app.MapStudioInAppMessaging();
app.MapStudioMessageAttachments();
app.MapStudioOperationsEnhancements();
app.MapStudioScheduling();
app.MapStudioProduction();
app.MapWorkspaceAccess();
app.MapStudioEmployeeDirectory();
app.MapStudioDemoData();
new WorkspaceJobStore(app.Services.GetRequiredService<StudioDatabase>()).EnsureSchema();
StudioVersionSchema.EnsureCreated(app.Services.GetRequiredService<StudioDatabase>());
app.MapWorkspaceJobs();
app.MapMediaProduction();
app.MapMediaBulkSelection();
app.MapProductionItems();
app.MapProductionOrders();
app.MapCommercialProduction();
app.MapJobWorkflowGraphs();
app.MapFrontDeskPosLaunch();
app.MapJobLifecycle();
new FrontDeskBookingRequestStore(app.Services.GetRequiredService<StudioDatabase>()).EnsureSchema();
Aegis.Studio.Web.Workflow.WorkflowSchema.Ensure(app.Services.GetRequiredService<StudioDatabase>());
app.MapFrontDeskBookingRequests();
app.MapConnectedWorkflow();
Aegis.Studio.Web.Finance.FinanceReleaseStore.MapStudioFinance(app);
app.MapStudioClients();
app.MapStudioUnifiedMessaging();
app.MapStudioEmailAssociations();
app.MapStudioFulfillment();
Aegis.Studio.Web.Reminders.ClientReminderEndpoints.MapClientReminders(app);
Aegis.Studio.Web.Intake.IntakeSchema.Ensure(app.Services.GetRequiredService<StudioDatabase>());
Aegis.Studio.Web.Intake.IntakeRosterStore.Map(app);
Aegis.Studio.Web.Intake.SubjectMessages.Map(app);
app.MapStudioQuotes();
app.MapJobAccess();
app.MapStudioServiceStatus();
app.MapJobWorkspaceParticipation();
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
Aegis.Studio.Web.PublicApi.PublicV1Endpoints.MapPublicV1(app);
Aegis.Studio.Web.PublicApi.PublicWebhookWorker.EnsureSchema(app.Services.GetRequiredService<StudioDatabase>());

app.MapGet("/api/email/status", async (global::Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphMailboxClient client, global::Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphEmailOptions options, UnroutedEmailStore unrouted, CancellationToken cancellationToken) =>
{
    string? slackWebhook = await secretProvider.GetAsync("Notifications:SlackWebhookUrl", cancellationToken);
    string? teamsWebhook = await secretProvider.GetAsync("Notifications:TeamsWebhookUrl", cancellationToken);
    return Results.Ok(new
    {
        enabled = options.InboundEnabled,
        configured = client.IsConfigured,
        collectionMailbox = options.SenderUpn,
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
app.MapPost("/api/email/inbound", async (InboundEmail email, StudioEmailIntake intake, CancellationToken token) =>
{
    try
    {
        var result=await intake.ReceiveAsync(email,token);
        return result.Routed ? Results.Ok(result) : Results.UnprocessableEntity(result);
    }
    catch(InvalidOperationException ex){return Results.BadRequest(new{error=ex.Message});}
}).RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
app.MapGet("/api/email/unrouted", (UnroutedEmailStore store) => Results.Ok(store.GetOpen())).RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });
app.MapGet("/api/email/outbox", (ClaimsPrincipal user, OutboundEmailStore outbox, StudioStore store, WorkspaceAccessService access) =>
    Results.Ok(outbox.GetPending().Where(e => e.StudioMessageId is null && store.GetConversation(e.ConversationId) is { } c && access.HasPermission(user,c.WorkspaceId,"View"))));
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
    var permissions=access.GetPermissionMap(user);
    return Results.Ok(store.GetJobs(permissions.Where(p=>p.Value.AccessJob).Select(p=>p.Key).ToArray()).Select(j=>permissions[j.Id].ViewFinancials?j:j with{FinancialEvents=[]}));
});
app.MapGet("/api/jobs/{id:guid}", (Guid id, ClaimsPrincipal user, StudioStore store, JobAccessService access) =>
{
    if (!access.HasPermission(user, id, "ViewJob")) return Results.Forbid();
    return store.GetJob(id) is StudioJob job ? Results.Ok(access.HasPermission(user,id,"ViewFinancials")?job:job with{FinancialEvents=[]}) : Results.NotFound();
});
app.MapGet("/api/jobs/{id:guid}/timeline", (Guid id, int? count, ClaimsPrincipal user, StudioStore store, JobAccessService access, WorkspaceTimelineService timeline) =>
{
    if (store.GetJob(id) is null) return Results.NotFound();
    if (!access.HasPermission(user, id, "ViewJob")) return Results.Forbid();
    return Results.Ok(timeline.GetForJob(id, count ?? 200));
});
app.MapPut("/api/jobs/{id:guid}", (Guid id, UpdateJobRequest request, ClaimsPrincipal user, StudioStore store, JobAccessService access) =>
    WorkflowEndpoints.Run(()=>
    {
        if (!access.HasPermission(user,id,"EditJob")) return Results.Forbid();
        return Results.Ok(store.UpdateJob(id,request));
    }));

// Pipeline cards open the Job action panel. Workspace transfer uses the same versioned command.
app.MapPost("/api/jobs/{id:guid}/pipeline-transition", (Guid id, PipelineJobTransitionRequest request, ClaimsPrincipal user, WorkflowStore workflow) =>
    WorkflowEndpoints.Run(()=>
    {
        if(!string.IsNullOrWhiteSpace(request.Status)) return Results.BadRequest(new{error="Open the Job and use its workflow action to change stages."});
        return Results.Ok(workflow.Execute(id,new WorkflowCommand("transfer",request.ExpectedRevision,request.OperationId,Reason:request.Reason,WorkspaceId:request.WorkspaceId),user));
    }));

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
    if(request.ApplyToJobBalance != (context.Items["StudioPosAction"] as string == "TakePayment")) return Results.BadRequest(new{error="Use the requested Front Desk financial operation."});
    if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => string.IsNullOrWhiteSpace(x.Description) || x.UnitPrice < 0m || x.Quantity <= 0)) return Results.BadRequest(new { error = "At least one valid sale line is required." });
    try { Sale sale = pos.Create(jobId, request, studio, context.User.Identity?.Name ?? "Studio", context.User.IsInRole("Administrator") || context.User.IsInRole("Operations Manager")); return Results.Created($"/api/pos/sales/{sale.Id}", sale); } catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
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




