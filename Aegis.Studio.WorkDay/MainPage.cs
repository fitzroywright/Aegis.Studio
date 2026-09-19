namespace Aegis.Studio.WorkDay;

public sealed class MainPage:ContentPage
{
    private readonly WorkDayLocalStore store; private readonly WorkDaySyncEngine sync;
    private readonly Label connectivity=new(){Text="UNKNOWN",FontAttributes=FontAttributes.Bold};
    private readonly Label workState=new(){Text="SIGNED OUT",FontSize=22,FontAttributes=FontAttributes.Bold};
    private readonly Label pending=new(){Text="0 changes pending"};
    private readonly VerticalStackLayout list=new(){Spacing=10};
    private readonly Button workButton=new(){Text="SIGN IN FOR WORK",FontSize=18,HeightRequest=58};
    private LocalWorkSession? active;
    public MainPage(WorkDayLocalStore store,WorkDaySyncEngine sync){this.store=store;this.sync=sync;Title="My WorkDay";workButton.Clicked+=ToggleWork;Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=14,Children={new Label{Text="AEGIS STUDIO WORKDAY",FontSize=26,FontAttributes=FontAttributes.Bold},new Label{Text=DateTime.Now.ToString("dddd, MMMM d")},workState,workButton,new Label{Text="Connectivity"},connectivity,pending,new Label{Text="MY WORKDAY",FontSize=20,FontAttributes=FontAttributes.Bold},list}}};Loaded+=async(_,_)=>await LoadAsync();}
    private async Task LoadAsync(){await store.InitializeAsync();active=await store.ActiveSessionAsync();RenderWorkState();await TrySync();RenderAssignments(await store.GetAssignmentsAsync());}
    private async Task TrySync(){try{await sync.SynchronizeAsync();connectivity.Text="ONLINE";}catch{connectivity.Text="OFFLINE — LOCAL WORK SAFE";}pending.Text=$"{(await store.PendingAsync()).Count} changes pending";}
    private void RenderAssignments(IEnumerable<LocalAssignment> values){list.Children.Clear();foreach(var a in values){Button open=new(){Text=$"{a.StartsAtUtc.ToLocalTime():HH:mm}  {a.Title}\n{a.Location??"Location not set"}  ·  {a.Status}",HorizontalOptions=LayoutOptions.Fill,HeightRequest=74};open.Clicked+=async(_,_)=>await Navigation.PushAsync(new AssignmentPage(a,store,sync));list.Children.Add(open);}}
    private void RenderWorkState(){bool on=active?.EndedAtUtc is null;workState.Text=on?$"SIGNED IN · Started {active!.StartedAtUtc.ToLocalTime():t}":"SIGNED OUT";workButton.Text=on?"SIGN OUT":"SIGN IN FOR WORK";}
    private async void ToggleWork(object? s,EventArgs e){bool online=connectivity.Text=="ONLINE";if(active?.EndedAtUtc is null){active.EndedAtUtc=DateTimeOffset.UtcNow;active.EndedOffline=!online;await store.SaveWorkSessionAsync(active);await Queue("WorkSignOut",null,!online?"offline":null);active=null;}else{active=new LocalWorkSession{Id=Guid.NewGuid(),UserName="",StartedAtUtc=DateTimeOffset.UtcNow,DeviceId=DeviceInfo.Current.Idiom.ToString(),StartedOffline=!online};await store.SaveWorkSessionAsync(active);await Queue("WorkSignIn",null,!online?"offline":null);}RenderWorkState();await TrySync();}
    private Task Queue(string kind,Guid? assignment,string? text)=>store.QueueAsync(new LocalOperation{OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=assignment,OccurredAtUtc=DateTimeOffset.UtcNow,DeviceId=DeviceInfo.Current.Idiom.ToString(),Text=text,State=nameof(LocalSyncState.Pending)});
}

public sealed class AssignmentPage:ContentPage
{
    public AssignmentPage(LocalAssignment a,WorkDayLocalStore store,WorkDaySyncEngine sync){Title=a.Title;Button arrived=Action("ARRIVED","Arrive");Button start=Action("START SESSION","StartSession");Button complete=Action("COMPLETE SESSION","CompleteSession");Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={new Label{Text=a.CustomerName??a.Title,FontSize=26,FontAttributes=FontAttributes.Bold},new Label{Text=$"{a.StartsAtUtc.ToLocalTime():f} — {a.EndsAtUtc.ToLocalTime():t}"},new Label{Text=a.Location??"Location not set"},new Label{Text=a.Instructions??"No special instructions."},arrived,start,complete}}};Button Action(string label,string kind){Button b=new(){Text=label,HeightRequest=58};b.Clicked+=async(_,_)=>{await store.QueueAsync(new LocalOperation{OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=a.Id,OccurredAtUtc=DateTimeOffset.UtcNow,DeviceId=DeviceInfo.Current.Idiom.ToString(),ExpectedVersion=a.Version});try{await sync.SynchronizeAsync();}catch{}await DisplayAlertAsync("WorkDay","Action safely recorded.","OK");};return b;}}
}
