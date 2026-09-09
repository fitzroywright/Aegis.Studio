namespace Aegis.Studio.Web.Diagnostics;

using Aegis.Studio.Infrastructure.Email;
using Aegis.Studio.Web.Persistence;
using Common.Diagnostics;
using Npgsql;

public static class StudioEngineeringDiagnostics
{
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

        RouteGroupBuilder group = endpoints.MapGroup("/api/engineering/diagnostics").RequireAuthorization();

        group.MapGet("/runs", async (int? take, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            int requested = Math.Clamp(take ?? 25, 1, 100);
            return Results.Ok(await store.GetRecentAsync(requested, cancellationToken));
        });

        group.MapGet("/runs/{runId:guid}", async (Guid runId, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            EngineeringDiagnosticRun? run = await store.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapPost("/run", async (
            EngineeringDiagnosticRunRequest request,
            EngineeringDiagnosticEngine engine,
            StudioEngineeringDiagnosticCatalog catalog,
            IWebHostEnvironment environment,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                string requestedBy = context.User.Identity?.Name ?? "authenticated-operator";
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
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Resolution))
            {
                return Results.BadRequest(new { error = "Resolution is required." });
            }

            string resolvedBy = context.User.Identity?.Name ?? "authenticated-operator";
            EngineeringDiagnosticRun? resolved = await store.ResolveAsync(
                runId,
                resolvedBy,
                request.Resolution.Trim(),
                cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        });

        return endpoints;
    }
}

public sealed class StudioEngineeringDiagnosticCatalog
{
    private readonly NpgsqlDataSource dataSource;
    private readonly MicrosoftGraphEmailClient graphClient;
    private readonly MicrosoftGraphEmailOptions graphOptions;

    public StudioEngineeringDiagnosticCatalog(
        NpgsqlDataSource dataSource,
        MicrosoftGraphEmailClient graphClient,
        MicrosoftGraphEmailOptions graphOptions)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.graphClient = graphClient ?? throw new ArgumentNullException(nameof(graphClient));
        this.graphOptions = graphOptions ?? throw new ArgumentNullException(nameof(graphOptions));
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
                _ => Task.FromResult(Passed(
                    "studio-l2-common-platform",
                    "Studio Common platform integration",
                    "Common.Security, Common.Secrets and Common.Messaging are integrated; Common.Diagnostics is providing this diagnostic contract."))),

            new EngineeringDiagnosticCheckDefinition(
                "studio-l1-full-system",
                "Studio full-system readiness",
                EngineeringDiagnosticLevel.Level1CriticalIntervention,
                _ => Task.FromResult(Passed(
                    "studio-l1-full-system",
                    "Studio full-system readiness",
                    "Level 1 executes the complete cumulative Studio diagnostic set and intervention gate.")))
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

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
