using Common.Diagnostics;
using Aegis.Studio.Web.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Common.Registration;

namespace Aegis.Studio.Web;

public sealed class OperationsTelemetryPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    EngineeringDiagnosticEngine diagnosticEngine,
    StudioEngineeringDiagnosticCatalog diagnosticCatalog,
    ILogger<OperationsTelemetryPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "Aegis.Studio";
    private const string DisplayName = "Aegis Studio";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? operationsUrl = configuration["Aegis:Operations:Url"];
        if (string.IsNullOrWhiteSpace(operationsUrl))
        {
            logger.LogInformation("Operations telemetry is disabled because Aegis:Operations:Url is not configured.");
            return;
        }

        string instanceId = configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
        string identityFile = configuration["Aegis:Registration:IdentityFile"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Studio", "registration-identity.json")
                : "/var/lib/aegis/studio/registration-identity.json");

        var identityStore = new FileRegistrationIdentityStore(identityFile);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RegistrationIdentityDocument identity =
                    await identityStore.LoadOrCreateAsync(ApplicationId, instanceId, stoppingToken);

                if (string.IsNullOrWhiteSpace(identity.Credential))
                {
                    logger.LogDebug("Operations telemetry not published because the application registration has no durable credential yet.");
                }
                else
                {
                    EngineeringDiagnosticRun? diagnosticRun = null;
                    try
                    {
                        diagnosticRun = await diagnosticEngine.RunAsync(
                            EngineeringDiagnosticLevel.Level4Analysis,
                            "Aegis.Studio",
                            environment.EnvironmentName,
                            "Aegis.Operations",
                            null,
                            diagnosticCatalog.Build(),
                            stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Operations diagnostic publication could not collect current evidence.");
                    }

                    string operationalState = diagnosticRun is null
                        ? "Unknown"
                        : ToOperationalState(diagnosticRun.Status);

                    using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        operationsUrl.TrimEnd('/') + "/api/operations/applications/observe")
                    {
                        Content = JsonContent.Create(new
                        {
                            applicationId = ApplicationId,
                            displayName = DisplayName,
                            instanceId,
                            environment = environment.EnvironmentName,
                            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                            registrationStatus = "Registered",
                            state = operationalState,
                            observedAtUtc = DateTimeOffset.UtcNow,
                            configurationStatus = "Published",
                            dependencies = Array.Empty<string>(),
                            reason = diagnosticRun is null
                                ? "Process heartbeat received but diagnostic evidence could not be collected."
                                : $"Aggregate state is based on Common.Diagnostics Level 4 run {diagnosticRun.RunId}."
                        })
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

                    using HttpResponseMessage response = await client.SendAsync(request, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogDebug(
                            "Operations heartbeat returned HTTP {StatusCode}; application remains operational.",
                            (int)response.StatusCode);
                    }

                    await PublishFlowDefinitionAsync(
                        client,
                        operationsUrl,
                        identity,
                        stoppingToken);

                    if (diagnosticRun is not null)
                    {
                        await PublishDiagnosticsAsync(
                            client,
                            operationsUrl,
                            identity,
                            diagnosticRun,
                            stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Operations telemetry heartbeat failed; application remains operational.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private static async Task PublishFlowDefinitionAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            operationsUrl.TrimEnd('/') + "/api/operations/flow-definitions")
        {
            Content = JsonContent.Create(new
            {
                applicationId = ApplicationId,
                flowType = "WorkspaceMessage",
                displayName = "Studio Workspace Message",
                stages = new object[]
                {
                new { name = "MailRouting", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                new { name = "Attachments", parentStage = "MailRouting", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "Workspace", parentStage = "Attachments", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                new { name = "Email", parentStage = "Workspace", branch = "Email", warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "Slack", parentStage = "Workspace", branch = "Slack", warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "WhatsApp", parentStage = "Workspace", branch = "WhatsApp", warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "InApp", parentStage = "Workspace", branch = "InApp", warningAfterSeconds = 10, criticalAfterSeconds = 60 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Flow metadata is advisory to Operations and must never affect application availability.
        }
    }


    private static string ToOperationalState(EngineeringDiagnosticStatus status) =>
        status switch
        {
            EngineeringDiagnosticStatus.Passed => "Healthy",
            EngineeringDiagnosticStatus.Warning => "Warning",
            EngineeringDiagnosticStatus.Failed => "Failed",
            EngineeringDiagnosticStatus.InterventionRequired => "Degraded",
            _ => "Unknown"
        };

    private static async Task PublishDiagnosticsAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        EngineeringDiagnosticRun run,
        CancellationToken ct)
    {
        foreach (EngineeringDiagnosticCheckResult check in run.Checks)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                operationsUrl.TrimEnd('/') + "/api/operations/diagnostics/observe")
            {
                Content = JsonContent.Create(new
                {
                    diagnosticId = ApplicationId + ":" + check.CheckId,
                    applicationId = ApplicationId,
                    component = check.Name,
                    category = "Health",
                    state = ToOperationalState(check.Status),
                    summary = check.Summary,
                    observedAtUtc = run.CompletedAt,
                    detail = check.Evidence,
                    correlationId = run.RunId.ToString("D"),
                    runbook = "Studio / Diagnostics"
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
            request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", run.RunId.ToString("N"));

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                break;
        }
    }

}
