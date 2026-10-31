using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.Persistence;
using Aegis.Studio.Web.Production;
using Aegis.Studio.Web.Scheduling;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace Aegis.Studio.Web.Bookings;

public sealed record PublicBookingSessionType(string Id,string Name,string Description,int DurationMinutes,int SetupBufferMinutes,int CleanupBufferMinutes,long PriceMinor,long DepositMinor,string Currency,IReadOnlyList<string> Locations,IReadOnlyDictionary<string,int> ResourceRequirements,bool IsActive,bool IsPublished,Guid? DefaultWorkspaceId=null,string? Category=null);
public sealed record PublicBookingCatalog(long Revision,IReadOnlyList<PublicBookingSessionType> SessionTypes);
public sealed record PublicBookingHours(string LocationCode,int DayOfWeek,string OpensAt,string ClosesAt,int SlotStepMinutes);
public sealed record PublicBookingPublicationRequest(bool IsPublished,long ExpectedRevision);

public sealed class StudioPublicBookingCatalogStore(StudioDatabase database)
{
    static readonly JsonSerializerOptions JsonOptions=new(JsonSerializerDefaults.Web);
    public void EnsureSchema()
    {
        using var c=database.OpenConnection();
        using var q=new NpgsqlCommand("create table if not exists studio_public_booking_catalog_state(id integer primary key check(id=1),revision bigint not null default 0); create table if not exists studio_public_booking_session_types(id text primary key,payload jsonb not null); create table if not exists studio_public_booking_hours(location_code text not null,day_of_week integer not null,opens_at time not null,payload jsonb not null,primary key(location_code,day_of_week,opens_at)); insert into studio_public_booking_catalog_state(id,revision) values(1,0) on conflict(id) do nothing",c);q.ExecuteNonQuery();
    }
    public PublicBookingCatalog Get(bool publishedOnly=false)
    {
        using var c=database.OpenConnection();long revision;
        using(var q=new NpgsqlCommand("select revision from studio_public_booking_catalog_state where id=1",c))revision=(long)q.ExecuteScalar()!;
        using var list=new NpgsqlCommand("select payload::text from studio_public_booking_session_types order by lower(payload->>'name')",c);
        using var r=list.ExecuteReader();var rows=new List<PublicBookingSessionType>();
        while(r.Read()){var item=JsonSerializer.Deserialize<PublicBookingSessionType>(r.GetString(0),JsonOptions)!;if(!publishedOnly||item.IsPublished&&item.IsActive)rows.Add(item);}
        return new(revision,rows);
    }
    public PublicBookingCatalog Save(IReadOnlyList<PublicBookingSessionType> items,long? expectedRevision=null)
    {
        Validate(items);using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var item in items.Where(x=>x.DefaultWorkspaceId.HasValue))
        {
            using var verify=new NpgsqlCommand("select count(*) from studio_workspaces where id=$1 and is_enabled",c,tx);
            verify.Parameters.AddWithValue(item.DefaultWorkspaceId!.Value);
            if(Convert.ToInt64(verify.ExecuteScalar())!=1)throw new ArgumentException("Default Workspace must be an enabled Studio workspace.");
        }
        using(var lockRow=new NpgsqlCommand("select revision from studio_public_booking_catalog_state where id=1 for update",c,tx))
        {
            long current=(long)lockRow.ExecuteScalar()!;
            if(expectedRevision is long expected&&current!=expected)throw new InvalidOperationException("Booking settings changed. Reload before saving.");
        }
        using(var clear=new NpgsqlCommand("delete from studio_public_booking_session_types",c,tx))clear.ExecuteNonQuery();
        foreach(var item in items){using var q=new NpgsqlCommand("insert into studio_public_booking_session_types(id,payload) values($1,$2::jsonb)",c,tx);q.Parameters.AddWithValue(item.Id);q.Parameters.AddWithValue(JsonSerializer.Serialize(item,JsonOptions));q.ExecuteNonQuery();}
        using(var bump=new NpgsqlCommand("update studio_public_booking_catalog_state set revision=revision+1 where id=1 returning revision",c,tx)){long revision=(long)bump.ExecuteScalar()!;tx.Commit();return new(revision,items);}
    }
    public IReadOnlyList<PublicBookingHours> GetHours()
    {
        using var c=database.OpenConnection();using var q=new NpgsqlCommand("select payload::text from studio_public_booking_hours order by location_code,day_of_week,opens_at",c);using var r=q.ExecuteReader();var values=new List<PublicBookingHours>();while(r.Read())values.Add(JsonSerializer.Deserialize<PublicBookingHours>(r.GetString(0),JsonOptions)!);return values;
    }
    public object SetPublication(string id,PublicBookingPublicationRequest request)
    {
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using var guard=new NpgsqlCommand("select revision from studio_public_booking_catalog_state where id=1 for update",c,tx);
        long current=(long)guard.ExecuteScalar()!;
        if(current!=request.ExpectedRevision)throw new InvalidOperationException("Booking settings changed. Reload before changing online booking.");
        using var find=new NpgsqlCommand("select payload::text from studio_public_booking_session_types where id=$1",c,tx);find.Parameters.AddWithValue(id);
        if(find.ExecuteScalar() is not string payload)throw new KeyNotFoundException("Save this service before enabling online booking.");
        var row=JsonSerializer.Deserialize<PublicBookingSessionType>(payload,JsonOptions)!;
        if(request.IsPublished&&!row.IsActive)throw new InvalidOperationException("Activate and save this service before enabling online booking.");
        if(row.IsPublished==request.IsPublished){tx.Commit();return new{revision=current,sessionType=row};}
        row=row with{IsPublished=request.IsPublished};
        using var update=new NpgsqlCommand("update studio_public_booking_session_types set payload=$2::jsonb where id=$1",c,tx);
        update.Parameters.AddWithValue(id);update.Parameters.AddWithValue(JsonSerializer.Serialize(row,JsonOptions));update.ExecuteNonQuery();
        using var bump=new NpgsqlCommand("update studio_public_booking_catalog_state set revision=revision+1 where id=1 returning revision",c,tx);
        long revision=(long)bump.ExecuteScalar()!;tx.Commit();return new{revision,sessionType=row};
    }
    public long SaveHours(IReadOnlyList<PublicBookingHours> hours,long? expectedRevision=null)
    {
        var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(hours.Count>56)throw new ArgumentException("Configure up to four opening windows per location and weekday.");
        foreach(var x in hours){if(x.LocationCode is not ("BK" or "BE")||x.DayOfWeek is <0 or >6||!TimeOnly.TryParse(x.OpensAt,out var opens)||!TimeOnly.TryParse(x.ClosesAt,out var closes)||closes<=opens||x.SlotStepMinutes is <5 or >240||!keys.Add(x.LocationCode+":"+x.DayOfWeek+":"+opens.ToString("HH:mm")))throw new ArgumentException("Hours need unique opening and closing ranges in Jamaica time for each Studio location and weekday.");}
        foreach(var day in hours.GroupBy(x=>x.LocationCode+":"+x.DayOfWeek)){var ordered=day.OrderBy(x=>TimeOnly.Parse(x.OpensAt)).ToArray();for(int i=1;i<ordered.Length;i++)if(TimeOnly.Parse(ordered[i].OpensAt)<TimeOnly.Parse(ordered[i-1].ClosesAt))throw new ArgumentException("Opening windows for the same location and weekday cannot overlap.");}
        using var c=database.OpenConnection();using var tx=c.BeginTransaction();
        using(var guard=new NpgsqlCommand("select revision from studio_public_booking_catalog_state where id=1 for update",c,tx))
        {
            long current=(long)guard.ExecuteScalar()!;
            if(expectedRevision is long expected&&current!=expected)throw new InvalidOperationException("Booking settings changed. Reload before saving hours.");
        }
        using(var clear=new NpgsqlCommand("delete from studio_public_booking_hours",c,tx))clear.ExecuteNonQuery();
        foreach(var x in hours){using var q=new NpgsqlCommand("insert into studio_public_booking_hours(location_code,day_of_week,opens_at,payload) values($1,$2,$3,$4::jsonb)",c,tx);q.Parameters.AddWithValue(x.LocationCode);q.Parameters.AddWithValue(x.DayOfWeek);q.Parameters.AddWithValue(TimeOnly.Parse(x.OpensAt));q.Parameters.AddWithValue(JsonSerializer.Serialize(x,JsonOptions));q.ExecuteNonQuery();}
        using var bump=new NpgsqlCommand("update studio_public_booking_catalog_state set revision=revision+1 where id=1 returning revision",c,tx);long revision=(long)bump.ExecuteScalar()!;tx.Commit();return revision;
    }
    static void Validate(IReadOnlyList<PublicBookingSessionType> items)
    {
        if(items.Count>200)throw new ArgumentException("A maximum of 200 public session types is supported.");
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var x in items)
        {
            string id=x.Id??"";
            if(!Regex.IsMatch(id,@"^[A-Za-z0-9._-]{1,100}$")||!ids.Add(id))throw new ArgumentException("Session type IDs must be unique stable values.");
            if(string.IsNullOrWhiteSpace(x.Name)||x.Name.Trim().Length>160||!names.Add(x.Name.Trim()))throw new ArgumentException("Session names must be unique and are limited to 160 characters.");
            if((x.Category?.Length??0)>80)throw new ArgumentException("Session categories are limited to 80 characters.");
            if(x.Description?.Length>1000||x.DurationMinutes is <15 or >1440||x.SetupBufferMinutes is <0 or >480||x.CleanupBufferMinutes is <0 or >480||x.PriceMinor<0||x.DepositMinor<0||x.DepositMinor>x.PriceMinor||x.Currency!="JMD")throw new ArgumentException("Set a JMD price and deposit, a valid duration, and buffers of no more than eight hours.");
            if(x.Locations is null||x.Locations.Count==0||x.Locations.Any(l=>l is not ("BK" or "BE"))||x.Locations.Distinct().Count()!=x.Locations.Count)throw new ArgumentException("Choose Kings Plaza (BK), East Queen Street (BE), or both.");
            if(x.ResourceRequirements is null||x.ResourceRequirements.Any(p=>!Regex.IsMatch(p.Key,@"^[A-Za-z0-9._-]{1,80}$")||p.Value is <0 or >20))throw new ArgumentException("Resource rules must use active Studio resource types and whole-number counts.");
        }
    }
}

public static class StudioPublicBookingCatalogEndpoints
{
    public static void MapStudioPublicBookingCatalog(this WebApplication app)
    {
        var group=app.MapGroup("/api/admin/public-booking/session-types").RequireAuthorization(new AuthorizeAttribute{Roles="Administrator"});
        group.MapGet("/settings",(IConfiguration configuration)=>
        {
            string? bookingUrl=null;
            if(Uri.TryCreate(configuration["PublicSite:WebBaseUrl"]??configuration["PublicSite:BaseUrl"],UriKind.Absolute,out var origin)&&
                origin.Scheme=="https"&&string.IsNullOrEmpty(origin.UserInfo)&&origin.AbsolutePath=="/"&&string.IsNullOrEmpty(origin.Query)&&string.IsNullOrEmpty(origin.Fragment))
                bookingUrl=new Uri(origin,"/#booking").AbsoluteUri;
            return Results.Ok(new{bookingUrl,publishingEnabled=configuration.GetValue<bool>("PublicSite:BookingCatalogPublishingEnabled")});
        });
        group.MapGet("/",(StudioPublicBookingCatalogStore store)=>Results.Ok(store.Get()));
        group.MapGet("/workspaces",(StudioDatabase db)=>{using var c=db.OpenConnection();using var q=new NpgsqlCommand("select id,name from studio_workspaces where is_enabled order by sort_order,name",c);using var r=q.ExecuteReader();var items=new List<object>();while(r.Read())items.Add(new{id=r.GetGuid(0),name=r.GetString(1)});return Results.Ok(items);});
        group.MapPut("/",(PublicBookingSessionType[] items,HttpRequest request,StudioPublicBookingCatalogStore store)=>{try{return Results.Ok(store.Save(items,ExpectedRevision(request)));}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}});
        group.MapPatch("/{id}/publication",(string id,PublicBookingPublicationRequest request,StudioPublicBookingCatalogStore store)=>
        {
            try{return Results.Ok(store.SetPublication(id,request));}
            catch(KeyNotFoundException e){return Results.NotFound(new{error=e.Message});}
            catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}
        });
        app.MapGet("/api/admin/public-booking/hours",(StudioPublicBookingCatalogStore store)=>Results.Ok(store.GetHours())).RequireAuthorization(new AuthorizeAttribute{Roles="Administrator"});
        app.MapPut("/api/admin/public-booking/hours",(PublicBookingHours[] hours,HttpRequest request,StudioPublicBookingCatalogStore store)=>{try{return Results.Ok(new{revision=store.SaveHours(hours,ExpectedRevision(request))});}catch(ArgumentException e){return Results.BadRequest(new{error=e.Message});}catch(InvalidOperationException e){return Results.Conflict(new{error=e.Message});}}).RequireAuthorization(new AuthorizeAttribute{Roles="Administrator"});
    }
    private static long? ExpectedRevision(HttpRequest request)
    {
        string value=request.Headers["If-Match"].ToString().Trim('"');
        if(string.IsNullOrEmpty(value))return null;
        if(!long.TryParse(value,out long revision)||revision<0)throw new ArgumentException("A valid booking revision is required.");
        return revision;
    }
}

internal sealed class PublicBookingSiteClient : IDisposable
{
    private readonly HttpClient http;

    internal PublicBookingSiteClient(IConfiguration configuration,HttpMessageHandler? handler=null)
    {
        ExternalIntegrationIsolation.RequireExternalIntegrations(configuration);
        var root=new Uri(configuration["PublicSite:BaseUrl"]??throw new InvalidOperationException("Configure PublicSite:BaseUrl."));
        if(root.Scheme!="https"||!string.IsNullOrEmpty(root.UserInfo)||root.AbsolutePath!="/"||!string.IsNullOrEmpty(root.Query)||!string.IsNullOrEmpty(root.Fragment))
            throw new InvalidOperationException("PublicSite:BaseUrl must be an HTTPS origin.");
        string secret=configuration["PublicSite:IntegrationToken"]??"";
        if(secret.Length<32)throw new InvalidOperationException("Configure a scoped public site integration token.");
        http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=root,Timeout=TimeSpan.FromSeconds(45)};
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",secret);
    }

    internal Task<JsonElement> PutCatalog(object data,string idempotencyKey,CancellationToken ct)
        => Put("/api/v1/admin/booking/catalog",data,idempotencyKey,ct);

    internal Task<JsonElement> PutAvailability(object data,string idempotencyKey,CancellationToken ct)
        => Put("/api/v1/admin/booking/availability",data,idempotencyKey,ct);

    private async Task<JsonElement> Put(string path,object data,string idempotencyKey,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Put,path){Content=JsonContent.Create(data)};
        request.Headers.Add("Idempotency-Key",idempotencyKey);
        using var response=await http.SendAsync(request,ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken:ct);
    }

    public void Dispose()=>http.Dispose();
}

public sealed class StudioPublicBookingCatalogPublisher(StudioPublicBookingCatalogStore store,StudioDatabase database,ScheduleStore schedule,IConfiguration configuration,ILogger<StudioPublicBookingCatalogPublisher> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if(!configuration.GetValue<bool>("PublicSite:BookingCatalogPublishingEnabled"))return;
        long published=-1,apiRevision=0;
        var photographers=new PhotographerStore(database);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                var catalog=store.Get(true);
                using var site=new PublicBookingSiteClient(configuration);
                if(catalog.Revision!=published||apiRevision==0)
                {
                    var body=new{sessionTypes=catalog.SessionTypes.Select(x=>new{id=x.Id,name=x.Name,description=x.Description,durationMinutes=x.DurationMinutes,setupBufferMinutes=x.SetupBufferMinutes,cleanupBufferMinutes=x.CleanupBufferMinutes,priceMinor=x.PriceMinor,depositMinor=x.DepositMinor,currency=x.Currency,locations=x.Locations,resourceRequirements=x.ResourceRequirements,active=x.IsActive})};
                    var result=await site.PutCatalog(body,$"studio-catalog-{catalog.Revision}",ct);apiRevision=result.GetProperty("revision").GetInt64();published=catalog.Revision;
                }
                var slots=StudioPublicBookingAvailabilityBuilder.Build(catalog,store.GetHours(),schedule,photographers,apiRevision,DateTimeOffset.UtcNow);
                var availability=new{slots};var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(availability,new JsonSerializerOptions(JsonSerializerDefaults.Web)))).ToLowerInvariant();
                await site.PutAvailability(availability,"availability-"+hash,ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception ex){logger.LogError(ex,"Studio public booking catalog publication will retry.");}
            try{await Task.Delay(TimeSpan.FromSeconds(60),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
        }
    }
}

public static class StudioPublicBookingAvailabilityBuilder
{
    public static IReadOnlyList<object> Build(PublicBookingCatalog catalog,IReadOnlyList<PublicBookingHours> hours,ScheduleStore schedule,PhotographerStore photographers,long apiRevision,DateTimeOffset now)
    {
        if(apiRevision<1||catalog.SessionTypes.Count==0||hours.Count==0)return [];
        TimeZoneInfo zone=TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica");DateTimeOffset localNow=TimeZoneInfo.ConvertTime(now,zone);DateOnly first=DateOnly.FromDateTime(localNow.DateTime);
        DateTimeOffset rangeStart=now.AddDays(-1),rangeEnd=now.AddDays(61);var occupancy=schedule.GetResourceOccupancy(rangeStart,rangeEnd);
        var resources=schedule.GetResources().Where(x=>x.IsActive).ToArray();var staff=photographers.GetAll().Where(x=>x.IsActive).ToArray();
        var leave=staff.ToDictionary(x=>x.ScheduleResourceId,x=>photographers.GetAvailability(x.UserName,rangeStart,rangeEnd).Where(a=>a.AvailabilityType is "Unavailable" or "Leave").ToArray());
        var result=new List<object>();
        for(int offset=0;offset<61;offset++)
        {
            DateOnly date=first.AddDays(offset);int day=(int)date.DayOfWeek;
            foreach(var window in hours.Where(x=>x.DayOfWeek==day))
            {
                if(!TimeOnly.TryParse(window.OpensAt,out var opens)||!TimeOnly.TryParse(window.ClosesAt,out var closes))continue;
                foreach(var session in catalog.SessionTypes.Where(x=>x.IsActive&&x.Locations.Contains(window.LocationCode,StringComparer.OrdinalIgnoreCase)))
                {
                    int minute=opens.Hour*60+opens.Minute+session.SetupBufferMinutes,lastMinute=closes.Hour*60+closes.Minute-session.DurationMinutes-session.CleanupBufferMinutes;
                    while(minute<=lastMinute)
                    {
                        TimeOnly start=TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute));
                        DateTime localStart=date.ToDateTime(start,DateTimeKind.Unspecified);DateTime localBusyStart=localStart.AddMinutes(-session.SetupBufferMinutes),localBusyEnd=localStart.AddMinutes(session.DurationMinutes+session.CleanupBufferMinutes);
                        var starts=TimeZoneInfo.ConvertTimeToUtc(localStart,zone);var busyStart=TimeZoneInfo.ConvertTimeToUtc(localBusyStart,zone);var busyEnd=TimeZoneInfo.ConvertTimeToUtc(localBusyEnd,zone);
                        if(starts>now.UtcDateTime&&HasResources(session.ResourceRequirements,resources,occupancy,leave,busyStart,busyEnd))
                        {
                            if(result.Count>=20_000)throw new InvalidOperationException("Public booking availability exceeds the API's 20,000-slot publication limit. Increase the slot interval or shorten the publication window.");
                            DateTimeOffset startUtc=new(starts,TimeSpan.Zero),endUtc=startUtc.AddMinutes(session.DurationMinutes);
                            string stable=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session.Id}|{window.LocationCode}|{startUtc:O}"))).ToLowerInvariant();
                            result.Add(new{id=stable,locationCode=window.LocationCode,sessionTypeId=session.Id,startsAt=startUtc,endsAt=endUtc,catalogRevision=apiRevision});
                        }
                        minute+=window.SlotStepMinutes;
                    }
                }
            }
        }
        return result;
    }
    static bool HasResources(IReadOnlyDictionary<string,int> required,IReadOnlyList<ScheduleResource> resources,IReadOnlyList<ScheduleResourceOccupancy> occupancy,IReadOnlyDictionary<Guid,PhotographerAvailability[]> leave,DateTimeOffset start,DateTimeOffset end)
    {
        var assigned=new HashSet<Guid>();
        foreach(var rule in required.Where(x=>x.Value>0).OrderByDescending(x=>x.Key.Equals("Photographer",StringComparison.OrdinalIgnoreCase)))
        {
            var candidates=rule.Key.Equals("Photographer",StringComparison.OrdinalIgnoreCase)?resources.Where(x=>leave.ContainsKey(x.Id)):resources.Where(x=>x.ResourceType.Equals(rule.Key,StringComparison.OrdinalIgnoreCase));
            int chosen=0;
            foreach(var resource in candidates.Where(x=>!assigned.Contains(x.Id)))
            {
                if(occupancy.Any(e=>e.ResourceId==resource.Id&&e.StartsAtUtc<end&&e.EndsAtUtc>start))continue;
                if(leave.TryGetValue(resource.Id,out var away)&&away.Any(x=>x.StartsAtUtc<end&&x.EndsAtUtc>start))continue;
                assigned.Add(resource.Id);chosen++;if(chosen>=rule.Value)break;
            }
            if(chosen<rule.Value)return false;
        }
        return true;
    }
}
