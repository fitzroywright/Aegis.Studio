namespace Aegis.Studio.Web.Diagnostics;

using Common.Messaging.Channels.MicrosoftGraph;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Storage;
using Common.Diagnostics;
using Common.Messaging;
using Common.Secrets;
using Common.Security.Abstractions;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

public static class StudioEngineeringDiagnostics
{
    private const string DefaultApiKeyHeader = "X-Aegis-Diagnostics-Key";
    private const string DefaultApiKeySecretName = "Studio:Diagnostics:ApiKey";

    public static IServiceCollection AddStudioEngineeringDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCommonDiagnostics();
        services.AddSingleton<StudioEngineeringDiagnosticCatalog>();
        return services;
    }

    public static IEndpointRouteBuilder MapStudioEngineeringDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup("/api/engineering/diagnostics").AllowAnonymous();

        group.MapGet("/runs", async (int? take, HttpContext context, IConfiguration configuration, ISecretProvider secrets, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, configuration, secrets, cancellationToken)) return Results.Unauthorized();
            int requested = Math.Clamp(take ?? 25, 1, 100);
            return Results.Ok(await store.GetRecentAsync(requested, cancellationToken));
        });

        group.MapGet("/runs/{runId:guid}", async (Guid runId, HttpContext context, IConfiguration configuration, ISecretProvider secrets, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, configuration, secrets, cancellationToken)) return Results.Unauthorized();
            EngineeringDiagnosticRun? run = await store.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapPost("/run", async (
            EngineeringDiagnosticRunRequest request,
            EngineeringDiagnosticEngine engine,
            StudioEngineeringDiagnosticCatalog catalog,
            IWebHostEnvironment environment,
            HttpContext context,
            IConfiguration configuration,
            ISecretProvider secrets,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, configuration, secrets, cancellationToken)) return Results.Unauthorized();
            try
            {
                string requestedBy = context.User.Identity?.Name ?? "Aegis.Diagnostics";
                EngineeringDiagnosticRun run = await engine.RunAsync(
                    request.Level,
                    "Aegis.Studio",
                    environment.EnvironmentName,
                    requestedBy,
                    request.Reason,
                    catalog.Build(),
                    cancellationToken);
                return Results.Ok(run);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/runs/{runId:guid}/resolve", async (
            Guid runId,
            EngineeringDiagnosticResolutionRequest request,
            IEngineeringDiagnosticRunStore store,
            HttpContext context,
            IConfiguration configuration,
            ISecretProvider secrets,
            CancellationToken cancellationToken) =>
        {
            if (!await IsAuthorizedAsync(context, configuration, secrets, cancellationToken)) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(request.Resolution))
            {
                return Results.BadRequest(new { error = "Resolution is required." });
            }

            string resolvedBy = context.User.Identity?.Name ?? "Aegis.Diagnostics";
            EngineeringDiagnosticRun? resolved = await store.ResolveAsync(
                runId,
                resolvedBy,
                request.Resolution.Trim(),
                cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        });

        return endpoints;
    }

    private static async Task<bool> IsAuthorizedAsync(
        HttpContext context,
        IConfiguration configuration,
        ISecretProvider secrets,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        string headerName = configuration["Diagnostics:ApiKeyHeader"] ?? DefaultApiKeyHeader;
        string secretName = configuration["Diagnostics:ApiKeySecretName"] ?? DefaultApiKeySecretName;
        string? expected = await secrets.GetAsync(secretName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expected) || !context.Request.Headers.TryGetValue(headerName, out var supplied))
        {
            return false;
        }

        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied.ToString());
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

public sealed class StudioEngineeringDiagnosticCatalog
{
    private readonly NpgsqlDataSource dataSource;
    private readonly MicrosoftGraphMailboxClient graphClient;
    private readonly MicrosoftGraphEmailOptions graphOptions;
    private readonly ISecretProvider secretProvider;
    private readonly AttachmentStore attachmentStore;
    private readonly IServiceProvider services;

    public StudioEngineeringDiagnosticCatalog(
        NpgsqlDataSource dataSource,
        MicrosoftGraphMailboxClient graphClient,
        MicrosoftGraphEmailOptions graphOptions,
        ISecretProvider secretProvider,
        AttachmentStore attachmentStore,
        IServiceProvider services)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        this.graphOptions = graphOptions ?? throw new ArgumentNullException(nameof(graphOptions));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        this.attachmentStore = attachmentStore ?? throw new ArgumentNullException(nameof(attachmentStore));
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IReadOnlyList<EngineeringDiagnosticCheckDefinition> Build()
    {
        return
        [
            new EngineeringDiagnosticCheckDefinition(
                "studio-l5-process",
                "Studio application process",
                EngineeringDiagnosticLevel.Level5Scan,
                _ => Task.FromResult(Passed("studio-l5-process", "Studio application process", "Studio web application is running."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-database",
                "Studio PostgreSQL database",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckDatabaseAsync),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-security",
                "Common.Security",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(services.GetService<IAuthenticator>() is not null
                    ? Passed("studio-l4-security", "Common.Security", "Common.Security authenticator is registered in the Studio runtime.")
                    : Failed("studio-l4-security", "Common.Security", "Common.Security authenticator is not registered.", "IAuthenticator was not resolvable."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-secrets",
                "Common.Secrets",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckSecretsAsync),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-messaging",
                "Common.Messaging",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckMessagingAsync),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-storage",
                "Common.Storage",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckStorageAsync),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l4-client-gallery",
                "Client gallery provider",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(services.GetRequiredService<IConfiguration>()["ClientGallery:Provider"]=="Manifest"
                    ? Passed("studio-l4-client-gallery","Client gallery provider","Gallery handoff is configured. Delivery uses Common.Storage; the consuming client site requires separate verification.")
                    : Warning("studio-l4-client-gallery","Client gallery provider","No supported client gallery handoff provider is configured."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l3-email",
                "Studio Microsoft Graph email",
                EngineeringDiagnosticLevel.Level3Verification,
                CheckGraphEmailAsync),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l2-common-platform",
                "Studio Common platform integration",
                EngineeringDiagnosticLevel.Level2Repair,
                _ => Task.FromResult(services.GetService<IExternalDeliveryQueueHealth>() is not null
                    ? Passed(
                        "studio-l2-common-platform",
                        "Studio Common platform integration",
                        "Common.Diagnostics, Common.Security, Common.Secrets, Common.Messaging and Common.Storage are integrated into the Studio runtime.")
                    : Warning(
                        "studio-l2-common-platform",
                        "Studio Common platform integration",
                        "Common.Storage is integrated. Common.Messaging still requires a durable runtime queue before the complete Common platform is green."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l1-full-system",
                "Studio full-system readiness",
                EngineeringDiagnosticLevel.Level1CriticalIntervention,
                _ => Task.FromResult(Passed(
                    "studio-l1-full-system",
                    "Studio full-system readiness",
                    "Level 1 executes the complete cumulative Studio diagnostic set; component warnings/failures determine final readiness.")))
        ];
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckGraphEmailAsync(CancellationToken cancellationToken)
    {
        MicrosoftGraphMailboxVerification verification = await graphClient.VerifyConfigurationAsync(cancellationToken).ConfigureAwait(false);
        string evidence = $"State={verification.State}; Authentication={verification.AuthenticationSucceeded}; MailboxAccessible={verification.MailboxAccessible}; SecretPresent={verification.ClientSecretConfigured}; GraphStatus={verification.GraphStatusCode?.ToString() ?? "none"}";
        if (!graphOptions.InboundEnabled)
        {
            return Warning("studio-l3-email", "Studio Microsoft Graph email", "Microsoft Graph email routing is disabled.", evidence);
        }

        return verification.AuthenticationSucceeded && verification.MailboxAccessible
            ? Passed("studio-l3-email", "Studio Microsoft Graph email", "Microsoft Graph credentials are valid and the configured mailbox is accessible.", evidence)
            : Failed("studio-l3-email", "Studio Microsoft Graph email", verification.Detail, evidence);
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command = new("SELECT 1", connection);
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(value) == 1
                ? Passed("studio-l4-database", "Studio PostgreSQL database", "PostgreSQL accepted a live query.")
                : Warning("studio-l4-database", "Studio PostgreSQL database", "PostgreSQL responded but the verification query returned an unexpected value.");
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return Failed("studio-l4-database", "Studio PostgreSQL database", "PostgreSQL connectivity failed.", exception.GetType().Name);
        }
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckSecretsAsync(CancellationToken cancellationToken)
    {
        if (secretProvider is not ISecretProviderHealth health)
        {
            return Warning("studio-l4-secrets", "Common.Secrets", "The active secret provider does not expose provider-health diagnostics.");
        }

        SecretProviderHealth result = await health.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsEnabled)
        {
            return Warning("studio-l4-secrets", "Common.Secrets", $"Secret provider {result.ProviderName} is disabled.");
        }

        return result.IsAvailable
            ? Passed("studio-l4-secrets", "Common.Secrets", $"Secret provider {result.ProviderName} is available.")
            : Failed("studio-l4-secrets", "Common.Secrets", $"Secret provider {result.ProviderName} is unavailable.", result.Message ?? "Provider health check failed.");
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckMessagingAsync(CancellationToken cancellationToken)
    {
        IExternalDeliveryQueueHealth? health = services.GetService<IExternalDeliveryQueueHealth>();
        if (health is null)
        {
            return Warning("studio-l4-messaging", "Common.Messaging", "Common.Messaging is referenced but no durable delivery queue health service is registered in the Studio runtime.");
        }

        ExternalDeliveryQueueHealth result = await health.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        string queueEvidence = $"Queue={result.QueueName ?? "default"}; Pending={result.Pending}; Retrying={result.Retrying}; DeadLettered={result.DeadLettered}; ExpiredLeases={result.ExpiredLeases}; OldestPending={result.OldestPendingAge?.ToString() ?? "none"}";
        if (!result.IsAvailable)
        {
            return Failed("studio-l4-messaging", "Common.Messaging", "The Common.Messaging delivery queue is unavailable.", queueEvidence);
        }

        IExternalMessageChannelDiagnostic[] providerDiagnostics = services
            .GetServices<IExternalMessageChannel>()
            .OfType<IExternalMessageChannelDiagnostic>()
            .ToArray();

        if (providerDiagnostics.Length == 0)
        {
            return result.DeadLettered > 0
                ? Warning("studio-l4-messaging", "Common.Messaging", $"Messaging queue is available with {result.DeadLettered} dead-lettered item(s); no external channel exposes live verification.")
                : Passed("studio-l4-messaging", "Common.Messaging", $"Messaging queue is available; pending={result.Pending}. No external channel exposes a live verification probe.");
        }

        List<MessagingProviderHealth> providers = [];
        foreach (IExternalMessageChannelDiagnostic diagnostic in providerDiagnostics)
            providers.Add(await diagnostic.VerifyProviderAsync(cancellationToken).ConfigureAwait(false));

        MessagingProviderHealth[] failed = providers
            .Where(provider => !provider.Configured || !provider.Reachable)
            .ToArray();
        string providerEvidence = string.Join(
            "; ",
            providers.Select(provider =>
                $"{provider.Provider}: Configured={provider.Configured}, Reachable={provider.Reachable}, State={provider.State}, Reason={provider.Reason ?? "none"}"));

        if (failed.Length > 0)
        {
            return Failed(
                "studio-l4-messaging",
                "Common.Messaging",
                $"{failed.Length} Common.Messaging provider verification check(s) failed.",
                $"{queueEvidence}; {providerEvidence}");
        }

        return result.DeadLettered > 0
            ? Warning("studio-l4-messaging", "Common.Messaging", $"Messaging queue is available and provider verification passed, but {result.DeadLettered} item(s) are dead-lettered.")
            : Passed("studio-l4-messaging", "Common.Messaging", $"Messaging queue is healthy and {providers.Count} live provider verification check(s) passed. {queueEvidence}");
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckStorageAsync(CancellationToken cancellationToken)
    {
        Common.Storage.StorageHealth result = await attachmentStore.CheckStorageHealthAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Available)
        {
            return Failed("studio-l4-storage", "Common.Storage", "Common.Storage is unavailable.", result.Error ?? "Storage provider health check failed.");
        }

        return result.Writable
            ? Passed("studio-l4-storage", "Common.Storage", $"Common.Storage provider {result.Provider} is available and writable.")
            : Warning("studio-l4-storage", "Common.Storage", $"Common.Storage provider {result.Provider} is reachable but not writable.");
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary, string? evidence = null) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary, evidence);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary, string? evidence = null) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary, evidence);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
