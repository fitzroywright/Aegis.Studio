using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.WorkDay;

public sealed class MainPage:ContentPage
{
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly WorkDayUploadService uploads;
    private readonly WorkDayLocationService location;
    private readonly WorkDayDeviceIdentity device;
    private readonly WorkDaySessionContext session;
    private readonly WorkDayApiClient api;
    private readonly WorkDayCredentialStore credentials;
    private readonly IServiceProvider services;

    private readonly Label connectivity=new(){Text="UNKNOWN",FontAttributes=FontAttributes.Bold};
    private readonly Label workState=new(){Text="SIGNED OUT",FontSize=22,FontAttributes=FontAttributes.Bold};
    private readonly Label tracking=new(){Text="Location Tracking: Inactive"};
    private readonly Label pending=new(){Text="0 changes pending"};
    private readonly Label uploadCount=new(){Text="0 files pending upload"};
    private readonly Label lastSync=new(){Text="Last Sync: Never"};
    private readonly Label timeSummary=new(){Text="Regular 0h · Overtime 0h · Weekend 0h"};
    private readonly Label attention=new(){Text="Nothing needs attention."};
    private readonly SearchBar search=new(){Placeholder="Search today's/upcoming customer or session"};
    private readonly VerticalStackLayout searchResults=new(){Spacing=8};
    private readonly VerticalStackLayout todayList=new(){Spacing=10};
    private readonly VerticalStackLayout upcomingList=new(){Spacing=10};
    private readonly Button workButton=new(){Text="SIGN IN FOR WORK",FontSize=18,HeightRequest=58};
    private LocalWorkSession? active;
    private LocalAssignment? nextAssignment;

    public MainPage(
        WorkDayLocalStore store,
        WorkDaySyncEngine sync,
        WorkDayUploadService uploads,
        WorkDayLocationService location,
        WorkDayDeviceIdentity device,
        WorkDaySessionContext session,
        WorkDayApiClient api,
        WorkDayCredentialStore credentials,
        IServiceProvider services)
    {
        this.store=store;this.sync=sync;this.uploads=uploads;this.location=location;this.device=device;this.session=session;this.api=api;this.credentials=credentials;this.services=services;
        Title="My WorkDay";
        workButton.Clicked+=ToggleWork;
        search.TextChanged+=async(_,_)=>RenderSearch(await store.GetAssignmentsAsync());

        Button refresh=new(){Text="SYNC NOW",HeightRequest=48};
        refresh.Clicked+=async(_,_)=>await RefreshAsync();
        Button messages=new(){Text="MESSAGES",HeightRequest=48};
        messages.Clicked+=async(_,_)=>await Navigation.PushAsync(new WorkDayMessagesPage(api));
        Button availability=new(){Text="AVAILABILITY / LEAVE",HeightRequest=48};
        availability.Clicked+=async(_,_)=>await Navigation.PushAsync(new WorkDayAvailabilityPage(api,store));
        Button logout=new(){Text="LOG OUT",HeightRequest=44};
        logout.Clicked+=Logout;

        Content=new ScrollView
        {
            Content=new VerticalStackLayout
            {
                Padding=18,Spacing=14,
                Children=
                {
                    new Label{Text="AEGIS STUDIO WORKDAY",FontSize=26,FontAttributes=FontAttributes.Bold},
                    new Label{Text=DateTime.Now.ToString("dddd, MMMM d")},
                    new Label{Text=session.DisplayName??session.UserName??"Field User",FontAttributes=FontAttributes.Bold},
                    workState,tracking,workButton,
                    new Grid
                    {
                        ColumnDefinitions={new ColumnDefinition(GridLength.Star),new ColumnDefinition(GridLength.Star)},
                        Children=
                        {
                            Panel("Connectivity",connectivity,0),
                            Panel("Synchronization",lastSync,1)
                        }
                    },
                    pending,uploadCount,
                    new Label{Text="TIME (LAST 14 DAYS)",FontSize=18,FontAttributes=FontAttributes.Bold},
                    timeSummary,
                    search,searchResults,
                    refresh,messages,availability,
                    new Label{Text="NEEDS ATTENTION",FontSize=19,FontAttributes=FontAttributes.Bold},
                    attention,
                    new Label{Text="TODAY",FontSize=20,FontAttributes=FontAttributes.Bold},
                    todayList,
                    new Label{Text="UPCOMING",FontSize=20,FontAttributes=FontAttributes.Bold},
                    upcomingList,
                    logout
                }
            }
        };
        Loaded+=async(_,_)=>await LoadAsync();
    }

    private static Border Panel(string title,Label value,int column)
    {
        VerticalStackLayout stack=new(){Spacing=4,Children={new Label{Text=title,FontAttributes=FontAttributes.Bold},value}};
        Border border=new(){Content=stack,Padding=12,StrokeThickness=1};
        Grid.SetColumn(border,column);
        return border;
    }

    private async Task LoadAsync()
    {
        await store.InitializeAsync();
        active=await store.ActiveSessionAsync();
        RenderWorkState();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        bool online=false;
        try
        {
            await sync.SynchronizeAsync();
            location.Configure(sync.LocationTrackingEnabled);
            await uploads.ProcessAsync();
            online=sync.State=="Healthy";
        }
        catch(HttpRequestException){}
        catch(TaskCanceledException){}
        connectivity.Text=online?"ONLINE":"OFFLINE — LOCAL WORK SAFE";
        connectivity.TextColor=online?Colors.Green:Colors.DarkOrange;
        lastSync.Text=sync.LastSuccessUtc is null?"Last Sync: Never":$"Last Sync: {sync.LastSuccessUtc.Value.ToLocalTime():t}";
        pending.Text=$"{(await store.PendingAsync()).Count} changes pending";
        uploadCount.Text=$"{await store.PendingUploadCountAsync()} files pending upload";
        IReadOnlyList<LocalAssignment> assignments=await store.GetAssignmentsAsync();
        RenderAssignments(assignments);
        await RenderAttentionAsync(assignments);
        ApplyTrackingPolicy();
        RenderSearch(assignments);
        if(online)
        {
            await PublishObservationAsync();
            try
            {
                WorkDayTimeSummaryDto? summary=await api.GetTimeSummaryAsync();
                if(summary is not null)
                    timeSummary.Text=$"Regular {summary.RegularMinutes/60d:0.0}h · Overtime {summary.OvertimeMinutes/60d:0.0}h · Weekend {summary.WeekendMinutes/60d:0.0}h";
            }
            catch { }
        }
    }

    private void RenderSearch(IReadOnlyList<LocalAssignment> values)
    {
        searchResults.Children.Clear();
        string query=search.Text?.Trim()??"";
        if(query.Length<2)return;
        foreach(LocalAssignment a in values.Where(x=>
            (x.Title?.Contains(query,StringComparison.OrdinalIgnoreCase)??false) ||
            (x.CustomerName?.Contains(query,StringComparison.OrdinalIgnoreCase)??false) ||
            x.Id.ToString("D").Contains(query,StringComparison.OrdinalIgnoreCase)).Take(10))
            searchResults.Children.Add(AssignmentCard(a));
    }

    private async Task PublishObservationAsync()
    {
        try
        {
            IReadOnlyList<LocalOperation> pendingOps=await store.PendingAsync();
            string deviceId=await device.GetAsync();
            await api.PublishObservationAsync(new WorkDayClientObservationDto(
                deviceId,DateTimeOffset.UtcNow,"Online",sync.State,pendingOps.Count,
                pendingOps.FirstOrDefault()?.OccurredAtUtc,await store.PendingUploadCountAsync(),
                await store.FailedUploadCountAsync(),active?.EndedAtUtc is null,location.PermissionState));
        }
        catch { }
    }

    private void RenderAssignments(IReadOnlyList<LocalAssignment> values)
    {
        todayList.Children.Clear();upcomingList.Children.Clear();
        DateTime today=DateTime.Today;
        nextAssignment=values.Where(x=>x.EndsAtUtc.ToLocalTime()>=DateTimeOffset.Now&&x.Status!="Completed"&&x.Status!="Cancelled").OrderBy(x=>x.StartsAtUtc).FirstOrDefault();
        foreach(LocalAssignment a in values.Where(x=>x.StartsAtUtc.ToLocalTime().Date==today))
            todayList.Children.Add(AssignmentCard(a));
        foreach(LocalAssignment a in values.Where(x=>x.StartsAtUtc.ToLocalTime().Date>today).Take(20))
            upcomingList.Children.Add(AssignmentCard(a));
        if(todayList.Children.Count==0)todayList.Children.Add(new Label{Text="No assignments scheduled for today."});
        if(upcomingList.Children.Count==0)upcomingList.Children.Add(new Label{Text="No upcoming assignments cached."});
    }

    private View AssignmentCard(LocalAssignment a)
    {
        Button open=new()
        {
            Text=$"{a.StartsAtUtc.ToLocalTime():HH:mm}  {a.Title}\n{a.Location??"Location not set"}  ·  {a.Status}",
            HorizontalOptions=LayoutOptions.Fill,HeightRequest=78
        };
        open.Clicked+=async(_,_)=>await Navigation.PushAsync(
            ActivatorUtilities.CreateInstance<AssignmentPage>(services,a));
        return open;
    }

    private async Task RenderAttentionAsync(IReadOnlyList<LocalAssignment> assignments)
    {
        int pendingOps=(await store.PendingAsync()).Count;
        int pendingFiles=await store.PendingUploadCountAsync();
        List<string> items=[];
        if(pendingOps>0)items.Add($"{pendingOps} change(s) waiting to synchronize");
        if(pendingFiles>0)items.Add($"{pendingFiles} file(s) waiting to upload");
        bool locationExpected=sync.LocationTrackingEnabled && active?.EndedAtUtc is null && !string.Equals(sync.LocationTrackingMode,"Disabled",StringComparison.OrdinalIgnoreCase);
        if(locationExpected && location.PermissionState is "Denied" or "Unavailable")
            items.Add($"Location evidence unavailable during active work session ({location.PermissionState})");
        if(locationExpected && location.LastSamplingAttemptUtc is DateTimeOffset attempt && DateTimeOffset.UtcNow-attempt>TimeSpan.FromHours(2))
            items.Add("Location evidence has not been sampled for more than 2 hours");
        foreach(LocalAssignment a in assignments.Where(x=>x.EndsAtUtc<DateTimeOffset.UtcNow&&x.Status is not ("Completed" or "Cancelled")).Take(3))
            items.Add($"Incomplete: {a.Title}");
        try
        {
            if(connectivity.Text=="ONLINE")
            {
                List<NotificationDto>? notifications=await api.GetNotificationsAsync();
                int unread=notifications?.Count(x=>x.Channel=="InApp"&&!x.IsRead)??0;
                if(unread>0)items.Add($"{unread} unread notification(s)");
            }
        }
        catch{}
        attention.Text=items.Count==0?"Nothing needs attention.":string.Join(Environment.NewLine,items.Select(x=>"• "+x));
    }

    private void RenderWorkState()
    {
        bool on=active?.EndedAtUtc is null;
        workState.Text=on?$"SIGNED IN · Started {active!.StartedAtUtc.ToLocalTime():t}":"SIGNED OUT";
        workButton.Text=on?"SIGN OUT":"SIGN IN FOR WORK";
        tracking.Text=!sync.LocationTrackingEnabled
            ?"Location Tracking: Disabled by Studio policy"
            :!on
                ?"Location Tracking: Inactive"
                :location.SamplingState=="SuspendedByOS"
                ?"WORK SESSION ACTIVE · Location Tracking: OS SUSPENDED / CONTINUITY UNKNOWN"
                :location.TrackingActive
                    ?$"WORK SESSION ACTIVE · Location Tracking: {location.SamplingState}"
                    :"WORK SESSION ACTIVE · Location Tracking: Policy/availability dependent";
    }

    private void ApplyTrackingPolicy()
    {
        bool activeWork=active?.EndedAtUtc is null;
        if(!sync.LocationTrackingEnabled||!activeWork){location.StopTracking();RenderWorkState();return;}
        if(string.Equals(sync.LocationTrackingMode,"BackgroundDuringWork",StringComparison.OrdinalIgnoreCase))
        {
            location.StartBatteryConsciousTracking(
                ()=>Task.FromResult<Guid?>(active?.Id),
                ()=>Task.FromResult<Guid?>(nextAssignment?.Id));
        }
        RenderWorkState();
    }

    private async void ToggleWork(object? sender,EventArgs e)
    {
        bool online=connectivity.Text=="ONLINE";
        string deviceId=await device.GetAsync();
        if(active?.EndedAtUtc is null)
        {
            LocalLocationObservation? endLocation=sync.LocationTrackingEnabled?await location.CaptureAsync(null,active.Id):null;
            active.EndedAtUtc=DateTimeOffset.UtcNow;active.EndedOffline=!online;await store.SaveWorkSessionAsync(active);
            await QueueAsync("WorkSignOut",null,!online?"offline":null,deviceId,endLocation);
            location.StopTracking();active=null;
        }
        else
        {
            active=new LocalWorkSession{Id=Guid.NewGuid(),UserName=session.UserName??"",StartedAtUtc=DateTimeOffset.UtcNow,DeviceId=deviceId,StartedOffline=!online};
            await store.SaveWorkSessionAsync(active);
            LocalLocationObservation? startLocation=sync.LocationTrackingEnabled?await location.CaptureAsync(null,active.Id):null;
            await QueueAsync("WorkSignIn",null,!online?"offline":null,deviceId,startLocation);
        }
        RenderWorkState();ApplyTrackingPolicy();await RefreshAsync();
    }

    private Task QueueAsync(string kind,Guid? assignment,string? text,string deviceId,LocalLocationObservation? loc=null)=>
        store.QueueAsync(new LocalOperation
        {
            OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=assignment,OccurredAtUtc=DateTimeOffset.UtcNow,
            DeviceId=deviceId,Text=text,State=nameof(LocalSyncState.Pending),
            LocationObservationId=loc?.ObservationId,Latitude=loc?.Latitude,Longitude=loc?.Longitude,AccuracyMetres=loc?.AccuracyMetres,
            LocationObservedAtUtc=loc?.ObservedAtUtc,LocationSource=loc?.Source
        });

    private async void Logout(object? sender,EventArgs e)
    {
        int pendingOps=(await store.PendingAsync()).Count;
        int pendingFiles=await store.PendingUploadCountAsync();
        if(pendingOps>0||pendingFiles>0)
        {
            await DisplayAlertAsync("Work still pending",$"{pendingOps} change(s) and {pendingFiles} file(s) are not yet acknowledged. WorkDay will keep them locally; logging out will not delete them.","OK");
        }
        string deviceId=await device.GetAsync();
        try{if(connectivity.Text=="ONLINE")await api.RevokeDeviceSessionAsync(deviceId);}catch{}
        credentials.Clear();
        api.SetDeviceToken(null);
        session.IsAuthenticated=false;
        Preferences.Default.Remove("WorkDay.UserName");Preferences.Default.Remove("WorkDay.DisplayName");
        Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<LoginPage>());
    }
}
