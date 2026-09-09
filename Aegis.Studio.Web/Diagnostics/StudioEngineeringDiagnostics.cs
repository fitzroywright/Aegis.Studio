namespace Aegis.Studio.Web.Diagnostics;

using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Persistence;
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
    private const string DefaultApiKeyEnvironmentVariable = "STUDIO_DIAGNOSTICS_KEY";

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

        group.MapGet("/runs", async (int? take, HttpContext context, IConfiguration configuration, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
            int requested = Math.Clamp(take ?? 25, 1, 100);
            return Results.Ok(await store.GetRecentAsync(requested, cancellationToken));
        });

        group.MapGet("/runs/{runId:guid}", async (Guid runId, HttpContext context, IConfiguration configuration, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
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
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
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
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
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

    private static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        string headerName = configuration["Diagnostics:ApiKeyHeader"] ?? DefaultApiKeyHeader;
        string environmentVariable = configuration["Diagnostics:ApiKeyEnvironmentVariable"] ?? DefaultApiKeyEnvironmentVariable;
        string? expected = Environment.GetEnvironmentVariable(environmentVariable);
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
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly MicrosoftGraphEmailOptions graphOptions;
    private readonly ISecretProvider secretProvider;
    private readonly IServiceProvider services;

    public StudioEngineeringDiagnosticCatalog(
        NpgsqlDataSource dataSource,
        MicrosoftGraphEmailClient graphClient,
        MicrosoftGraphEmailOptions graphOptions,
        ISecretProvider secretProvider,
        IServiceProvider services)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        this.graphOptions = graphOptions ?? throw new ArgumentNullException(nameof(graphOptions));
        this.secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
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
                _ => Task.FromResult(Warning(
                    "studio-l4-storage",
                    "Common.Storage",
                    "Studio still uses its application-local AttachmentStore; Common.Storage is not yet integrated into the runtime."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l3-email",
                "Studio Microsoft Graph email",
                EngineeringDiagnosticLevel.Level3Verification,
                _ => Task.FromResult(graphOptions.Enabled && graphClient.IsConfigured
                    ? Passed("studio-l3-email", "Studio Microsoft Graph email", "Microsoft Graph email routing is enabled and configured.")
                    : Warning("studio-l3-email", "Studio Microsoft Graph email", "Microsoft Graph email routing is not fully configured."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l2-common-platform",
                "Studio Common platform integration",
                EngineeringDiagnosticLevel.Level2Repair,
                _ => Task.FromResult(Warning(
                    "studio-l2-common-platform",
                    "Studio Common platform integration",
                    "Common.Diagnostics is active and Security/Secrets are integrated, but Common.Storage and the Common.Messaging runtime queue must be present before platform integration is fully green."))),

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
        if (!result.IsAvailable)
        {
            return Failed("studio-l4-messaging", "Common.Messaging", "The Common.Messaging delivery queue is unavailable.", result.Message);
        }

        return result.DeadLettered > 0
            ? Warning("studio-l4-messaging", "Common.Messaging", $"Messaging queue is available with {result.DeadLettered} dead-lettered item(s).")
            : Passed("studio-l4-messaging", "Common.Messaging", $"Messaging queue is available; pending={result.Pending}.");
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
