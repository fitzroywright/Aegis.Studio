using Common.Diagnostics;
using Aegis.Studio.Web.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Common.Registration;
using Aegis.Studio.Web.WorkDay;

namespace Aegis.Studio.Web;

public sealed class OperationsTelemetryPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    EngineeringDiagnosticEngine diagnosticEngine,
    StudioEngineeringDiagnosticCatalog diagnosticCatalog,
    WorkDayStore workDayStore,
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

                    await PublishLifecycleHeartbeatAsync(
                        client,
                        operationsUrl,
                        identity,
                        instanceId,
                        operationalState,
                        stoppingToken);

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

                    int staleSeconds = Math.Clamp(configuration.GetValue<int?>("WorkDay:TelemetryStaleSeconds") ?? 300, 60, 3600);
                    WorkDayFleetSnapshot workDay = workDayStore.GetFleetSnapshot(TimeSpan.FromSeconds(staleSeconds));
                    await PublishWorkDayDiagnosticsAsync(client, operationsUrl, identity, workDay, stoppingToken);
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
        object[] definitions =
        [
            new
            {
                applicationId = ApplicationId,
                flowType = "InboundMessage",
                displayName = "Studio Inbound Message",
                stages = new object[]
                {
                    new { name = "Mailbox", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Router", parentStage = "Mailbox", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Workspace", parentStage = "Router", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Conversation", parentStage = "Workspace", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Notification", parentStage = "Conversation", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "OutboundMessage",
                displayName = "Studio Outbound Message",
                stages = new object[]
                {
                    new { name = "User", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "SendingIdentity", parentStage = "User", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Message", parentStage = "SendingIdentity", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Provider", parentStage = "Message", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Recipient", parentStage = "Provider", branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 3600 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "NotificationFanout",
                displayName = "Studio Notification Fan-out",
                stages = new object[]
                {
                    new { name = "Event", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Email", parentStage = "Event", branch = "Email", warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Slack", parentStage = "Event", branch = "Slack", warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Teams", parentStage = "Event", branch = "Teams", warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "InApp", parentStage = "Event", branch = "InApp", warningAfterSeconds = 10, criticalAfterSeconds = 60 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "AttachmentProcessing",
                displayName = "Studio Attachment Processing",
                stages = new object[]
                {
                    new { name = "Received", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Storage", parentStage = "Received", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Metadata", parentStage = "Storage", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                    new { name = "Workspace", parentStage = "Metadata", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "JobFlow",
                displayName = "Studio Job Flow",
                stages = new object[]
                {
                    new { name = "Created", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 3600 },
                    new { name = "Assigned", parentStage = "Created", branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 3600 },
                    new { name = "Accepted", parentStage = "Assigned", branch = (string?)null, warningAfterSeconds = 600, criticalAfterSeconds = 7200 },
                    new { name = "Arrived", parentStage = "Accepted", branch = (string?)null, warningAfterSeconds = 600, criticalAfterSeconds = 7200 },
                    new { name = "InProgress", parentStage = "Arrived", branch = (string?)null, warningAfterSeconds = 1800, criticalAfterSeconds = 14400 },
                    new { name = "Completed", parentStage = "InProgress", branch = (string?)null, warningAfterSeconds = 1800, criticalAfterSeconds = 14400 },
                    new { name = "Synchronized", parentStage = "Completed", branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 1800 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "WorkDaySession",
                displayName = "Studio WorkDay Session",
                stages = new object[]
                {
                    new { name = "Assigned", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Accepted", parentStage = "Assigned", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Arrived", parentStage = "Accepted", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "InProgress", parentStage = "Arrived", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Completed", parentStage = "InProgress", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Synchronized", parentStage = "Completed", branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 1800 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "WorkDaySync",
                displayName = "Studio WorkDay Synchronization",
                stages = new object[]
                {
                    new { name = "LocalChange", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Pending", parentStage = "LocalChange", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Sending", parentStage = "Pending", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "ServerProcessing", parentStage = "Sending", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Acknowledged", parentStage = "ServerProcessing", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "WorkDayUpload",
                displayName = "Studio WorkDay Upload",
                stages = new object[]
                {
                    new { name = "Selected", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "LocalQueue", parentStage = "Selected", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Upload", parentStage = "LocalQueue", branch = (string?)null, warningAfterSeconds = 300, criticalAfterSeconds = 1800 },
                    new { name = "Storage", parentStage = "Upload", branch = (string?)null, warningAfterSeconds = 120, criticalAfterSeconds = 1200 },
                    new { name = "MetadataAssociation", parentStage = "Storage", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                    new { name = "Acknowledged", parentStage = "MetadataAssociation", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            },
            new
            {
                applicationId = ApplicationId,
                flowType = "GalleryPublish",
                displayName = "Studio Gallery Publish",
                stages = new object[]
                {
                    new { name = "InternalStorage", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Publish", parentStage = "InternalStorage", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "PublicGallery", parentStage = "Publish", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                    new { name = "Verification", parentStage = "PublicGallery", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            }
        ];

        foreach (object definition in definitions)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                operationsUrl.TrimEnd('/') + "/api/operations/flow-definitions")
            {
                Content = JsonContent.Create(definition)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
            request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                break;
        }
    }

    private static async Task PublishWorkDayDiagnosticsAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        WorkDayFleetSnapshot snapshot,
        CancellationToken ct)
    {
        string state = snapshot.Clients == 0 ? "Unknown"
            : snapshot.FailedUploads > 0 ? "Warning"
            : snapshot.OfflineOrStale > 0 ? "Warning"
            : "Healthy";
        string summary = snapshot.Clients == 0
            ? "No WorkDay client observations have been received."
            : $"WorkDay clients {snapshot.Clients}; recently online {snapshot.RecentlyOnline}; offline/stale {snapshot.OfflineOrStale}; pending sync {snapshot.PendingSync}; pending uploads {snapshot.PendingUploads}; failed uploads {snapshot.FailedUploads}.";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            operationsUrl.TrimEnd('/') + "/api/operations/diagnostics/observe")
        {
            Content = JsonContent.Create(new
            {
                diagnosticId = ApplicationId + ":WorkDayFleet",
                applicationId = ApplicationId,
                component = "Aegis Studio WorkDay",
                category = "MobileFleet",
                state,
                summary,
                observedAtUtc = snapshot.GeneratedAtUtc,
                detail = new
                {
                    snapshot.Clients,
                    snapshot.RecentlyOnline,
                    snapshot.OfflineOrStale,
                    snapshot.PendingSync,
                    snapshot.OldestPendingAtUtc,
                    snapshot.PendingUploads,
                    snapshot.FailedUploads
                },
                correlationId = "workday-fleet-" + snapshot.GeneratedAtUtc.ToUnixTimeSeconds(),
                runbook = "docs/WORKDAY-RUNBOOK.md"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", "workday-fleet-" + snapshot.GeneratedAtUtc.ToUnixTimeSeconds());
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return;
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
                    runbook = "docs/RUNBOOK.md"
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

    private static async Task PublishLifecycleHeartbeatAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        string instanceId,
        string operationalState,
        CancellationToken ct)
    {
        LifecycleEventOutcome outcome = operationalState switch
        {
            "Healthy" => LifecycleEventOutcome.Succeeded,
            "Warning" or "Degraded" => LifecycleEventOutcome.Warning,
            "Failed" or "Offline" => LifecycleEventOutcome.Failed,
            _ => LifecycleEventOutcome.Started
        };

        var sink = new HttpLifecycleEventSink(
            client,
            new HttpLifecycleEventSinkOptions(
                new Uri(operationsUrl.TrimEnd('/') + "/api/operations/lifecycle/events")),
            (request, item, _) =>
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
                request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
                request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
                request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
                return Task.CompletedTask;
            });

        var telemetry = new LifecycleTelemetry(sink);
        await telemetry.EmitAsync(
            LifecycleEvent.Create(
                ApplicationId,
                instanceId,
                "ApplicationLifecycle",
                operationalState,
                outcome,
                Guid.NewGuid().ToString("N"),
                code: "Heartbeat"),
            ct);
    }

}
