using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Text.Json;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Scheduling;
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
                await ProvisionAccountsAsync(http,stoppingToken);
                await PublishSnapshotsAsync(http,stoppingToken);
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

    private async Task ProvisionAccountsAsync(HttpClient http,CancellationToken ct)
    {
        using IServiceScope scope=scopes.CreateScope();
        StudioDatabase database=scope.ServiceProvider.GetRequiredService<StudioDatabase>();
        foreach(PhotographerProfile profile in new PhotographerStore(database).GetAll().Where(x=>x.IsActive))
        {
            ct.ThrowIfCancellationRequested();
            using var response=await http.PostAsJsonAsync("admin/workday/users",
                new{userName=profile.UserName,displayName=profile.DisplayName},JsonOptions,ct);
            if(!response.IsSuccessStatusCode)
            {
                logger.LogWarning("WorkDay account provisioning for {UserName} returned HTTP {StatusCode}.",profile.UserName,(int)response.StatusCode);
                continue;
            }
            WorkDayAccountProvision? result=await response.Content.ReadFromJsonAsync<WorkDayAccountProvision>(JsonOptions,ct);
            if(result?.EnrollmentToken is not {Length:>0} token)continue;

            string path=configuration["WorkDay:EnrollmentFile"]??"/var/lib/aegis-studio/workday-enrollment-codes.json";
            string directory=Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            try{File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);}catch(PlatformNotSupportedException){}
            List<WorkDayEnrollmentRecord> records=[];
            if(File.Exists(path))
            {
                try{records=JsonSerializer.Deserialize<List<WorkDayEnrollmentRecord>>(await File.ReadAllTextAsync(path,ct))??[];}
                catch(JsonException){logger.LogError("WorkDay enrollment file {Path} is invalid; refusing to overwrite it.",path);continue;}
            }
            if(records.Any(x=>x.UserName.Equals(profile.UserName,StringComparison.OrdinalIgnoreCase)))continue;
            records.Add(new(profile.UserName,profile.DisplayName,token,result.ExpiresAt));
            await File.WriteAllTextAsync(path,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}),ct);
            try{File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);}catch(PlatformNotSupportedException){}
            logger.LogInformation("Created one-time WorkDay enrollment for {UserName}; token stored in protected file {Path}.",profile.UserName,path);
        }
    }

    private async Task PublishSnapshotsAsync(HttpClient http,CancellationToken ct)
    {
        using IServiceScope scope=scopes.CreateScope();
        IServiceProvider services=scope.ServiceProvider;
        StudioDatabase database=services.GetRequiredService<StudioDatabase>();
        ScheduleStore schedules=services.GetRequiredService<ScheduleStore>();
        WorkDayStore store=services.GetRequiredService<WorkDayStore>();
        IConfiguration config=services.GetRequiredService<IConfiguration>();
        JobAccessService access=services.GetRequiredService<JobAccessService>();
        PhotographerStore photographers=new(database);
        DateTimeOffset now=DateTimeOffset.UtcNow;
        WorkDayTimePolicy policy=WorkDayTimeSemantics.FromConfiguration(config);
        foreach(PhotographerProfile profile in photographers.GetAll().Where(x=>x.IsActive))
        {
            ct.ThrowIfCancellationRequested();
            ClaimsPrincipal principal=new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier,profile.UserName),
                new Claim("login_name",profile.UserName),
                new Claim(ClaimTypes.Name,profile.DisplayName)
            ],"WorkDayExchange"));
            WorkDayBootstrap? bootstrap=WorkDayEndpoints.BuildBootstrap(principal,database,schedules,store,config,access);
            if(bootstrap is null)continue;
            var snapshot=new
            {
                bootstrap.UserName,bootstrap.DisplayName,bootstrap.ServerTimeUtc,bootstrap.Today,bootstrap.Upcoming,
                bootstrap.ActiveWorkSession,bootstrap.LocationTrackingEnabled,bootstrap.LocationTrackingMode,
                bootstrap.GeneratedAtUtc,bootstrap.FieldTargets,
                availability=photographers.GetAvailability(profile.UserName,now.AddDays(-1),now.AddDays(90)),
                timeSummary=store.GetTimeSummaries([profile.UserName],now.AddDays(-14),now,policy).Single()
            };
            try
            {
                using var published=await http.PutAsJsonAsync("admin/workday/snapshots",
                    new{userName=profile.UserName,revision=now.ToUnixTimeMilliseconds(),snapshot},JsonOptions,ct);
                if(!published.IsSuccessStatusCode)
                    logger.LogWarning("WorkDay snapshot publication for {UserName} returned HTTP {StatusCode}.",profile.UserName,(int)published.StatusCode);
            }
            catch(HttpRequestException ex)
            {
                logger.LogWarning(ex,"WorkDay snapshot publication failed for {UserName}; Studio will retry.",profile.UserName);
            }
        }
    }

    private async Task PullOperationsAsync(HttpClient http,CancellationToken ct)
    {
        using var response=await http.GetAsync("admin/workday/updates?after=0&limit=100&kind=Operation",ct);
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

    private sealed record WorkDayAccountProvision(string UserName,string DisplayName,bool EnrollmentRequired,string? EnrollmentToken,string? ExpiresAt);\n    private sealed record WorkDayEnrollmentRecord(string UserName,string DisplayName,string Token,string? ExpiresAt);\n    private sealed record WorkDayUpdateBatch(IReadOnlyList<WorkDayUpdate> Items);
    private sealed record WorkDayUpdate(long Sequence,Guid OperationId,string UserName,string Kind,JsonElement Payload,DateTimeOffset CreatedAt);
}
