namespace Aegis.Studio.WorkDay;

public sealed class WorkDayAvailabilityPage:ContentPage
{
    private readonly WorkDayApiClient api;
    private readonly WorkDayLocalStore store;
    private readonly VerticalStackLayout list=new(){Spacing=8};
    private readonly Label status=new(){Text="Availability is part of Studio scheduling."};

    public WorkDayAvailabilityPage(WorkDayApiClient api,WorkDayLocalStore store)
    {
        this.api=api;this.store=store;Title="Availability / Leave";
        Button refresh=new(){Text="REFRESH",HeightRequest=48};refresh.Clicked+=async(_,_)=>await RefreshAsync();
        Button tomorrow=new(){Text="MARK TOMORROW UNAVAILABLE",HeightRequest=52};tomorrow.Clicked+=async(_,_)=>await QuickUnavailableAsync();
        Button add=new(){Text="ADD AVAILABILITY / LEAVE",HeightRequest=52};add.Clicked+=async(_,_)=>await AddAsync();
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={
            new Label{Text="AVAILABILITY / LEAVE",FontSize=25,FontAttributes=FontAttributes.Bold},
            new Label{Text="Your availability is used by Studio scheduling and conflict checks."},
            status,refresh,tomorrow,add,list
        }}};
        Loaded+=async(_,_)=>await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        DateTimeOffset from=DateTimeOffset.Now.AddDays(-1),to=DateTimeOffset.Now.AddDays(90);
        try
        {
            List<PhotographerAvailabilityDto> remote=await api.GetAvailabilityAsync(from,to)??[];
            await store.ReplaceAvailabilityAsync(remote.Select(x=>new LocalAvailability{Id=x.Id,UserName=x.UserName,StartsAtUtc=x.StartsAtUtc,EndsAtUtc=x.EndsAtUtc,AvailabilityType=x.AvailabilityType,Note=x.Note,CreatedAtUtc=x.CreatedAtUtc}));
            status.Text="Connected · availability refreshed";
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {
            status.Text="Offline · showing last cached availability";
        }
        Render(await store.GetAvailabilityAsync());
    }

    private void Render(IReadOnlyList<LocalAvailability> values)
    {
        list.Children.Clear();
        foreach(LocalAvailability item in values.Where(x=>x.EndsAtUtc>=DateTimeOffset.UtcNow).Take(50))
            list.Children.Add(new Border{StrokeThickness=1,Padding=10,Content=new VerticalStackLayout{Spacing=3,Children={
                new Label{Text=item.AvailabilityType,FontAttributes=FontAttributes.Bold},
                new Label{Text=$"{item.StartsAtUtc.ToLocalTime():ddd, MMM d h:mm tt} — {item.EndsAtUtc.ToLocalTime():ddd, MMM d h:mm tt}"},
                new Label{Text=item.Note??string.Empty}
            }}});
        if(list.Children.Count==0)list.Children.Add(new Label{Text="No upcoming availability exceptions recorded."});
    }

    private async Task QuickUnavailableAsync()
    {
        DateTime tomorrow=DateTime.Today.AddDays(1);
        DateTimeOffset start=new(tomorrow,TimeZoneInfo.Local.GetUtcOffset(tomorrow));
        await SaveAsync(start.ToUniversalTime(),start.AddDays(1).ToUniversalTime(),"Unavailable","Added from Aegis Studio WorkDay");
    }

    private async Task AddAsync()
    {
        string type=await DisplayActionSheetAsync("Availability type","Cancel",null,"Unavailable","Leave","Preferred","Available");
        if(string.IsNullOrWhiteSpace(type)||type=="Cancel")return;
        string? startText=await DisplayPromptAsync("Start",$"Local date/time, e.g. {DateTime.Now.AddDays(1):yyyy-MM-dd HH:mm}");
        if(!DateTime.TryParse(startText,out DateTime startLocal))return;
        string? endText=await DisplayPromptAsync("End",$"Local date/time, e.g. {DateTime.Now.AddDays(1).AddHours(8):yyyy-MM-dd HH:mm}");
        if(!DateTime.TryParse(endText,out DateTime endLocal)||endLocal<=startLocal)return;
        string? note=await DisplayPromptAsync(type,"Optional note");
        DateTimeOffset start=new(startLocal,TimeZoneInfo.Local.GetUtcOffset(startLocal));
        DateTimeOffset end=new(endLocal,TimeZoneInfo.Local.GetUtcOffset(endLocal));
        await SaveAsync(start.ToUniversalTime(),end.ToUniversalTime(),type,note);
    }

    private async Task SaveAsync(DateTimeOffset start,DateTimeOffset end,string type,string? note)
    {
        try
        {
            PhotographerAvailabilityDto value=await api.AddAvailabilityAsync(start,end,type,note);
            List<LocalAvailability> cached=await store.GetAvailabilityAsync();
            cached.RemoveAll(x=>x.Id==value.Id);
            cached.Add(new LocalAvailability{Id=value.Id,UserName=value.UserName,StartsAtUtc=value.StartsAtUtc,EndsAtUtc=value.EndsAtUtc,AvailabilityType=value.AvailabilityType,Note=value.Note,CreatedAtUtc=value.CreatedAtUtc});
            await store.ReplaceAvailabilityAsync(cached);
            status.Text=$"{type} saved to Studio.";
            Render(await store.GetAvailabilityAsync());
        }
        catch(Exception ex){await DisplayAlertAsync("Availability","Studio must be reachable to change availability. Existing cached availability remains available.\n\n"+ex.Message,"OK");}
    }
}
