using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.WorkDay;

public sealed class MainPage:ContentPage
{
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly WorkDayDeviceIdentity device;
    private readonly WorkDaySessionContext session;
    private readonly WorkDayApiClient api;
    private readonly WorkDayCredentialStore credentials;
    private readonly IServiceProvider services;
    private readonly Label connectivity=new(){Text="Waiting for Studio",FontAttributes=FontAttributes.Bold};
    private readonly Label pending=new();
    private readonly Label lastSync=new(){Text="Last synchronization: Never"};
    private readonly Label attention=new();
    private readonly SearchBar search=new(){Placeholder="Search customer or session"};
    private readonly VerticalStackLayout todayList=new(){Spacing=8};
    private readonly VerticalStackLayout upcomingList=new(){Spacing=8};
    private bool refreshing;

    public MainPage(WorkDayLocalStore store,WorkDaySyncEngine sync,WorkDayDeviceIdentity device,
        WorkDaySessionContext session,WorkDayApiClient api,WorkDayCredentialStore credentials,IServiceProvider services)
    {
        this.store=store;this.sync=sync;this.device=device;this.session=session;this.api=api;this.credentials=credentials;this.services=services;
        Title="My WorkDay";
        Button refresh=new(){Text="SYNC NOW"};refresh.Clicked+=async(_,_)=>await RefreshAsync();
        Button availability=new(){Text="AVAILABILITY / LEAVE"};availability.Clicked+=async(_,_)=>await Navigation.PushAsync(new WorkDayAvailabilityPage(api,store));
        Button logout=new(){Text="LOG OUT"};logout.Clicked+=async(_,_)=>await LogoutAsync();
        search.TextChanged+=async(_,_)=>RenderAssignments(await store.GetAssignmentsAsync());
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={
            new Label{Text="PHOTOGRAPHER WORKDAY",FontSize=25,FontAttributes=FontAttributes.Bold},
            new Label{Text=session.DisplayName??session.UserName??"Photographer"},
            connectivity,lastSync,pending,refresh,availability,search,
            new Label{Text="NEEDS ATTENTION",FontAttributes=FontAttributes.Bold},attention,
            new Label{Text="TODAY",FontSize=20,FontAttributes=FontAttributes.Bold},todayList,
            new Label{Text="UPCOMING",FontSize=20,FontAttributes=FontAttributes.Bold},upcomingList,logout
        }}};
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();await store.InitializeAsync();await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if(refreshing)return;refreshing=true;
        try
        {
            bool reached=false;
            try{await sync.SynchronizeAsync();reached=true;connectivity.Text=sync.State=="Healthy"?"ONLINE":"CONNECTED — CHANGES NEED ATTENTION";}
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {connectivity.Text="OFFLINE / SYNC UNAVAILABLE — LOCAL REPORTS RETAINED";}
            lastSync.Text=sync.LastSuccessUtc is null?"Last synchronization: Never":$"Last synchronization: {sync.LastSuccessUtc.Value.ToLocalTime():g}";
            List<LocalOperation> outstanding=await store.UnacknowledgedAsync();
            pending.Text=$"{outstanding.Count} report(s) awaiting acknowledgement";
            attention.Text=outstanding.Count==0?"No pending reports.":string.Join(Environment.NewLine,
                outstanding.Take(8).Select(x=>$"{x.Kind}: {x.State}{(x.LastError is null?"":" — "+x.LastError)}"));
            RenderAssignments(await store.GetAssignmentsAsync());
            if(reached)try
            {
                await api.PublishObservationAsync(new(await device.GetAsync(),DateTimeOffset.UtcNow,"Online",sync.State,
                    outstanding.Count,outstanding.FirstOrDefault()?.OccurredAtUtc,0,0,false,"NotRequested"));
            }
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException){}
        }
        finally{refreshing=false;}
    }

    private void RenderAssignments(IReadOnlyList<LocalAssignment> values)
    {
        todayList.Children.Clear();upcomingList.Children.Clear();
        string query=search.Text?.Trim()??"";
        foreach(LocalAssignment a in values.Where(x=>query.Length==0||x.Title.Contains(query,StringComparison.OrdinalIgnoreCase)||
            (x.CustomerName?.Contains(query,StringComparison.OrdinalIgnoreCase)??false)).OrderBy(x=>x.StartsAtUtc))
        {
            Button open=new(){Text=$"{a.StartsAtUtc.ToLocalTime():ddd MMM dd HH:mm}  {a.Title}\n{a.Location??"Location not set"} · {a.Status}",HeightRequest=80};
            open.Clicked+=async(_,_)=>await Navigation.PushAsync(ActivatorUtilities.CreateInstance<AssignmentPage>(services,a));
            if(a.StartsAtUtc.ToLocalTime().Date<=DateTime.Today&&a.EndsAtUtc.ToLocalTime().Date>=DateTime.Today)todayList.Children.Add(open);
            else upcomingList.Children.Add(open);
        }
        if(todayList.Children.Count==0)todayList.Children.Add(new Label{Text="No matching sessions today."});
        if(upcomingList.Children.Count==0)upcomingList.Children.Add(new Label{Text="No other matching assignments cached."});
    }

    private async Task LogoutAsync()
    {
        int reports=(await store.UnacknowledgedAsync()).Count;
        int legacyFiles=await store.PendingUploadCountAsync();
        if(reports>0||legacyFiles>0)
        {
            await DisplayAlertAsync("Unsynchronized work",$"Synchronize {reports} report(s) before signing out. {legacyFiles} file(s) from the previous client remain preserved; contact Studio support to recover them.","OK");return;
        }
        try{await api.RevokeDeviceSessionAsync(await device.GetAsync());}
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {await DisplayAlertAsync("Logout","Studio must be reachable to revoke this device session.","OK");return;}
        credentials.Clear();api.SetDeviceToken(null);session.IsAuthenticated=false;
        Preferences.Default.Remove("WorkDay.UserName");Preferences.Default.Remove("WorkDay.DisplayName");
        Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<LoginPage>());
    }
}
