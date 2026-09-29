using Common.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Channels;
using Common.Registration;

namespace Aegis.Studio.Web;

public sealed record StudioFlowTelemetryItem(
    string CorrelationId,
    string FlowType,
    string Stage,
    string? Branch,
    DateTimeOffset ObservedAtUtc,
    string? BusinessId,
    string? Detail,
    bool Failed,
    string? StateOverride = null);

public sealed class StudioOperationsFlowPublisher(
    IConfiguration configuration,
    ILogger<StudioOperationsFlowPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "Aegis.Studio";
    private readonly ConcurrentDictionary<string, DateTimeOffset> firstObserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly StudioNotificationBranchTracker branchTracker = new();
    private readonly Channel<StudioFlowTelemetryItem> queue =
        Channel.CreateBounded<StudioFlowTelemetryItem>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public bool TryEnqueue(
        string correlationId,
        string stage,
        string? branch = null,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
        => TryEnqueueNotification(correlationId, branch ?? stage, businessId, detail, failed);

    public bool TryEnqueueInbound(
        string correlationId,
        string stage,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
        => Enqueue(correlationId, "InboundMessage", stage, null, businessId, detail, failed);

    public bool TryEnqueueOutbound(
        string correlationId,
        string stage,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
        => Enqueue(correlationId, "OutboundMessage", stage, null, businessId, detail, failed);

    public bool TryEnqueueOutboundUnknown(
        string correlationId,
        string stage,
        string? businessId = null,
        string? detail = null)
        => Enqueue(correlationId, "OutboundMessage", stage, null, businessId, detail, false, "Unknown");

    public bool TryEnqueueNotification(
        string correlationId,
        string branch,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
        => Enqueue(correlationId, "NotificationFanout", branch, branch, businessId, detail, failed);

    private bool Enqueue(
        string correlationId,
        string flowType,
        string stage,
        string? branch,
        string? businessId,
        string? detail,
        bool failed,
        string? stateOverride = null)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return false;
        return queue.Writer.TryWrite(new(
            correlationId.Trim(),
            flowType,
            stage,
            branch,
            DateTimeOffset.UtcNow,
            businessId,
            detail,
            failed,
            stateOverride));
    }

    public bool TryEnqueueAttachment(
        string correlationId,
        string stage,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
    {
        return Enqueue(correlationId, "AttachmentProcessing", stage, null, businessId, detail, failed);
    }

    public bool TryEnqueueGallery(
        string correlationId,
        string stage,
        string? businessId = null,
        string? detail = null,
        bool failed = false)
    {
        return Enqueue(correlationId, "GalleryPublish", stage, null, businessId, detail, failed);
    }

    public bool TryEnqueueWorkDaySession(string correlationId,string stage,string? businessId=null,string? detail=null,bool failed=false)
        => Enqueue(correlationId,"WorkDaySession",stage,null,businessId,detail,failed);

    public bool TryEnqueueWorkDaySync(string correlationId,string stage,string? businessId=null,string? detail=null,bool failed=false,string? stateOverride=null)
        => Enqueue(correlationId,"WorkDaySync",stage,null,businessId,detail,failed,stateOverride);

    public bool TryEnqueueWorkDayUpload(string correlationId,string stage,string? businessId=null,string? detail=null,bool failed=false)
        => Enqueue(correlationId,"WorkDayUpload",stage,null,businessId,detail,failed);

    public bool TryEnqueueWorkDayMessage(string correlationId,string stage,string? businessId=null,string? detail=null,bool failed=false)
        => Enqueue(correlationId,"WorkDayMessage",stage,null,businessId,detail,failed);

    public bool TryEnqueueJobFlow(string correlationId,string stage,string? businessId=null,string? detail=null,bool failed=false)
        => Enqueue(correlationId,"JobFlow",stage,null,businessId,detail,failed);


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (StudioFlowTelemetryItem item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await PublishAsync(item, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Studio Operations flow publication failed; Studio workflow remains committed.");
            }
        }
    }

    private async Task PublishAsync(StudioFlowTelemetryItem item, CancellationToken ct)
    {
        string? operationsUrl = configuration["Aegis:Operations:Url"];
        if (string.IsNullOrWhiteSpace(operationsUrl)) return;

        string instanceId = configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
        string identityFile = configuration["Aegis:Registration:IdentityFile"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "Studio", "registration-identity.json")
                : "/var/lib/aegis/studio/registration-identity.json");

        var store = new FileRegistrationIdentityStore(identityFile);
        RegistrationIdentityDocument identity =
            await store.LoadOrCreateAsync(ApplicationId, instanceId, ct);
        if (string.IsNullOrWhiteSpace(identity.Credential)) return;

        using HttpClient lifecycleClient = new() { Timeout = TimeSpan.FromSeconds(5) };
        var lifecycleSink = new HttpLifecycleEventSink(
            lifecycleClient,
            new HttpLifecycleEventSinkOptions(new Uri(operationsUrl.TrimEnd('/') + "/api/operations/lifecycle/events")),
            (request, lifecycleEvent, _) =>
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
                request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
                request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
                request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
                return Task.CompletedTask;
            });
        var lifecycle = new LifecycleTelemetry(lifecycleSink);
        LifecycleEventOutcome lifecycleOutcome = item.Failed
            ? LifecycleEventOutcome.Failed
            : string.Equals(item.StateOverride, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? LifecycleEventOutcome.Warning
                : LifecycleEventOutcome.Succeeded;
        await lifecycle.EmitAsync(
            LifecycleEvent.Create(
                ApplicationId,
                instanceId,
                item.FlowType,
                item.Stage,
                lifecycleOutcome,
                item.CorrelationId,
                relatedBusinessId: item.BusinessId),
            ct);

        string[] stageNames = StudioFlowSemantics.StageNames(item.FlowType);
        if (stageNames.Length == 0) stageNames = [item.Stage];
        int currentIndex = Array.FindIndex(stageNames, x => x.Equals(item.Stage, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0) currentIndex = 2;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset startedAt = firstObserved.GetOrAdd(
            item.FlowType + "|" + item.CorrelationId,
            item.ObservedAtUtc);
        object[] stages;
        if (item.FlowType == "NotificationFanout")
        {
            string key = item.FlowType + "|" + item.CorrelationId;
            if (!string.IsNullOrWhiteSpace(item.Branch))
            {
                branchTracker.Observe(
                    key,
                    item.Branch,
                    item.Failed,
                    item.ObservedAtUtc,
                    item.Failed ? item.Detail : null);
            }

            IReadOnlyDictionary<string, StudioNotificationBranchObservation> tracked = branchTracker.Get(key);
            var observed = tracked.ToDictionary(
                x => x.Key,
                x => (x.Value.State, x.Value.ObservedAtUtc, x.Value.Detail),
                StringComparer.OrdinalIgnoreCase);

            stages = StudioFlowSemantics.NotificationFanout(observed, startedAt)
                .Select(stage => (object)new
                {
                    name = stage.Name,
                    state = stage.State,
                    startedAtUtc = stage.StartedAtUtc,
                    completedAtUtc = stage.CompletedAtUtc,
                    warningAfterSeconds = stage.Name == "Event" ? 10 : 60,
                    criticalAfterSeconds = stage.Name == "Event" ? 60 : 600,
                    error = stage.Error,
                    parentStage = stage.ParentStage,
                    branch = stage.Branch
                })
                .ToArray();
        }
        else
        {
            stages = StudioFlowSemantics.Linear(
                    item.FlowType,
                    item.Stage,
                    item.ObservedAtUtc,
                    item.Failed,
                    item.Detail,
                    item.StateOverride)
                .Select((stage, index) => (object)new
                {
                    name = stage.Name,
                    state = stage.State,
                    startedAtUtc = stage.StartedAtUtc,
                    completedAtUtc = stage.CompletedAtUtc,
                    warningAfterSeconds = index <= 2 ? 30 : 60,
                    criticalAfterSeconds = index <= 2 ? 300 : 600,
                    error = stage.Error,
                    parentStage = stage.ParentStage,
                    branch = stage.Branch
                })
                .ToArray();
        }

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            operationsUrl.TrimEnd('/') + "/api/operations/flows/observe")
        {
            Content = JsonContent.Create(new
            {
                correlationId = item.CorrelationId,
                applicationId = ApplicationId,
                flowType = item.FlowType,
                instanceId,
                startedAtUtc = startedAt,
                observedAtUtc = now,
                currentStage = item.Stage,
                relatedBusinessId = item.BusinessId,
                runbook = "docs/RUNBOOK.md",
                stages
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", item.CorrelationId);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogDebug("Studio Operations flow publication returned HTTP {StatusCode}.", (int)response.StatusCode);
    }
}

