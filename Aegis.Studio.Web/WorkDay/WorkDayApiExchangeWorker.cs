using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aegis.Studio.Web.WorkDay;

namespace Aegis.Studio.Web.WorkDay;

/// <summary>
/// Studio-initiated synchronization with the public API Site. This process makes outbound
/// requests only; the API Site never calls back into Studio.
/// </summary>
public sealed class WorkDayApiExchangeWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<WorkDayApiExchangeWorker> logger) : BackgroundService
{
    private static readonly Uri ApiBase = new("https://api.bryanstudiosltd.com/api/v1/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? key=configuration["WorkDay:ApiSyncKey"];
        if(string.IsNullOrWhiteSpace(key))
        {
            logger.LogInformation("WorkDay API exchange is disabled until WorkDay:ApiSyncKey is configured.");
            return;
        }

        using HttpClient http=new(){BaseAddress=ApiBase,Timeout=TimeSpan.FromSeconds(30)};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key);
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PullOperationsAsync(http,stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception ex)
            {
                logger.LogError(ex,"WorkDay API exchange failed; Studio will retry.");
                try{await Task.Delay(TimeSpan.FromSeconds(20),stoppingToken);}
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            }
        }
    }

    private async Task PullOperationsAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100",ct);
        response.EnsureSuccessStatusCode();
        var batch=await response.Content.ReadFromJsonAsync<WorkDayUpdateBatch>(JsonOptions,ct)
            ??throw new InvalidOperationException("The API Site returned an empty WorkDay update batch.");

        foreach(WorkDayUpdate update in batch.Items)
        {
            ct.ThrowIfCancellationRequested();
            WorkDayOperationResult result;
            try
            {
                WorkDayOperationRequest request=update.Payload.Deserialize<WorkDayOperationRequest>(JsonOptions)
                    ??throw new InvalidOperationException("WorkDay operation payload is empty.");
                using IServiceScope scope=scopes.CreateScope();
                WorkDayStore store=scope.ServiceProvider.GetRequiredService<WorkDayStore>();
                result=store.Apply(update.UserName,request);
            }
            catch(InvalidOperationException ex)
            {
                result=new(update.OperationId,WorkDayOperationState.Conflict,DateTimeOffset.UtcNow,ex.Message,null);
            }
            catch(JsonException ex)
            {
                result=new(update.OperationId,WorkDayOperationState.Conflict,DateTimeOffset.UtcNow,"WorkDay update payload is invalid.",null);
                logger.LogWarning(ex,"Invalid WorkDay update {OperationId} received from API Site.",update.OperationId);
            }

            using var ack=await http.PostAsJsonAsync(
                $"admin/workday/updates/{update.OperationId:D}/result",
                new { state=result.State.ToString(), message=result.Message, serverVersion=result.ServerVersion, acknowledgedAtUtc=result.AcknowledgedAtUtc },
                ct);
            ack.EnsureSuccessStatusCode();
        }
    }

    private sealed record WorkDayUpdateBatch(IReadOnlyList<WorkDayUpdate> Items);
    private sealed record WorkDayUpdate(long Sequence,Guid OperationId,string UserName,string Kind,JsonElement Payload,DateTimeOffset CreatedAt);
}
