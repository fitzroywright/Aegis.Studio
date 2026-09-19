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
    bool Failed);

public sealed class StudioOperationsFlowPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<StudioOperationsFlowPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "Aegis.Studio";
    private readonly ConcurrentDictionary<string, DateTimeOffset> firstObserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (string State, DateTimeOffset AtUtc, string? Error)>> branchStates = new(StringComparer.OrdinalIgnoreCase);
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
        bool failed)
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
            failed));
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

        string[] stageNames = item.FlowType switch
        {
            "InboundMessage" => ["Mailbox", "Router", "Workspace", "Conversation", "Notification"],
            "OutboundMessage" => ["User", "SendingIdentity", "Message", "Provider", "Recipient"],
            "NotificationFanout" => ["Event", "Email", "Slack", "Teams", "InApp"],
            "AttachmentProcessing" => ["Received", "Storage", "Metadata", "Workspace"],
            "GalleryPublish" => ["InternalStorage", "Publish", "PublicGallery", "Verification"],
            _ => [item.Stage]
        };
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
            ConcurrentDictionary<string, (string State, DateTimeOffset AtUtc, string? Error)> state =
                branchStates.GetOrAdd(key, _ => new(StringComparer.OrdinalIgnoreCase));
            state["Event"] = ("Completed", startedAt, null);
            if (!string.IsNullOrWhiteSpace(item.Branch))
                state[item.Branch] = (item.Failed ? "Failed" : "Completed", item.ObservedAtUtc, item.Failed ? item.Detail : null);

            stages = stageNames.Select(name =>
            {
                bool isBranch = !name.Equals("Event", StringComparison.OrdinalIgnoreCase);
                bool seen = state.TryGetValue(name, out var snapshot);
                return (object)new
                {
                    name,
                    state = seen ? snapshot.State : isBranch ? "Pending" : "Completed",
                    startedAtUtc = seen ? snapshot.AtUtc : name == "Event" ? startedAt : (DateTimeOffset?)null,
                    completedAtUtc = seen && snapshot.State == "Completed" ? snapshot.AtUtc : name == "Event" ? startedAt : (DateTimeOffset?)null,
                    warningAfterSeconds = isBranch ? 60 : 10,
                    criticalAfterSeconds = isBranch ? 600 : 60,
                    error = seen ? snapshot.Error : null,
                    parentStage = isBranch ? "Event" : null,
                    branch = isBranch ? name : null
                };
            }).ToArray();
        }
        else
        {
            stages = stageNames.Select((name, index) => (object)new
            {
                name,
                state = index < currentIndex
                    ? "Completed"
                    : index == currentIndex
                        ? item.Failed ? "Failed" : "Completed"
                        : "Pending",
                startedAtUtc = index == currentIndex ? item.ObservedAtUtc : (DateTimeOffset?)null,
                completedAtUtc = index == currentIndex && !item.Failed ? now : (DateTimeOffset?)null,
                warningAfterSeconds = index <= 2 ? 30 : 60,
                criticalAfterSeconds = index <= 2 ? 300 : 600,
                error = index == currentIndex && item.Failed ? item.Detail : null,
                parentStage = index == 0 ? null : stageNames[index - 1],
                branch = (string?)null
            }).ToArray();
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
