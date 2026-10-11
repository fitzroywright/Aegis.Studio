using Aegis.Studio.WorkDay.Core;

namespace Aegis.Studio.WorkDay;

public sealed class AssignmentPage:ContentPage
{
    private readonly LocalAssignment assignment;
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly WorkDayDeviceIdentity device;
    private readonly Label status=new();
    private bool reporting;

    public AssignmentPage(LocalAssignment assignment,WorkDayLocalStore store,WorkDaySyncEngine sync,WorkDayDeviceIdentity device)
    {
        this.assignment=assignment;this.store=store;this.sync=sync;this.device=device;Title=assignment.Title;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={
            new Label{Text=assignment.Title,FontSize=25,FontAttributes=FontAttributes.Bold},
            new Label{Text=assignment.CustomerName??""},
            new Label{Text=$"{assignment.StartsAtUtc.ToLocalTime():f} — {assignment.EndsAtUtc.ToLocalTime():t}"},
            new Label{Text=assignment.Location??"Location not set"},new Label{Text=assignment.Instructions??"No special instructions."},status,
            Action("REVIEW REPORTS / RESOLVE CONFLICT",()=>Navigation.PushAsync(new WorkDayRecoveryPage(assignment.Id,store,sync))),
            Action("ACCEPT ASSIGNMENT",()=>ReportAsync("AcceptAssignment")),
            Action("DECLINE ASSIGNMENT",async()=>{string? reason=await DisplayPromptAsync("Decline assignment","Reason (required)");if(!string.IsNullOrWhiteSpace(reason))await ReportAsync("DeclineAssignment",reason);}),
            Action("ARRIVED",()=>ReportAsync("Arrive")),Action("START SESSION",()=>ReportAsync("StartSession")),
            Action("COMPLETE SESSION",()=>ReportAsync("CompleteSession")),
            Action("ADD NOTE",async()=>{string? text=await DisplayPromptAsync("Operational note","Note for the Job Owner and Workspace Manager");if(!string.IsNullOrWhiteSpace(text))await ReportAsync("AddNote",text);}),
            Action("REPORT A PROBLEM",async()=>{string? text=await DisplayPromptAsync("Report a problem","Describe the problem");if(!string.IsNullOrWhiteSpace(text))await ReportAsync("RecordException",text,"Other");}),
            Action("NAVIGATE",NavigateAsync),Action("CONTACT",ContactAsync),
            new Label{Text="Upload photographs in Studio after the session."}
        }}};
    }

    protected override async void OnAppearing(){base.OnAppearing();await RefreshStatusAsync();}
    private Button Action(string text,Func<Task> action)
    {
        Button b=new(){Text=text,HeightRequest=52};
        b.Clicked+=async(_,_)=>
        {
            if(reporting)return;reporting=true;b.IsEnabled=false;
            try{await action();}catch(Exception ex){await DisplayAlertAsync("Assignment",ex.Message,"OK");}
            finally{b.IsEnabled=true;reporting=false;}
        };return b;
    }

    private async Task RefreshStatusAsync()
    {
        LocalAssignment? current=await store.GetAssignmentAsync(assignment.Id);
        if(current is not null){assignment.Version=current.Version;assignment.Status=current.Status;assignment.IsCurrent=current.IsCurrent;}
        List<LocalOperation> pending=(await store.UnacknowledgedAsync()).Where(x=>x.AssignmentId==assignment.Id).ToList();
        string projected=assignment.Status;
        foreach(LocalOperation op in pending)projected=WorkDayClientRules.AssignmentStatus(op.Kind,op.Category,projected);
        status.Text=(assignment.IsCurrent?"":"Historical / no longer in current assignments · ")+$"Studio: {assignment.Status}"+(pending.Count==0?"":$" · Local: {projected} · {pending.Count} report(s) unacknowledged");
    }

    private async Task ReportAsync(string kind,string? text=null,string? category=null)
    {
        List<LocalOperation> outstanding=(await store.UnacknowledgedAsync()).Where(x=>x.AssignmentId==assignment.Id).ToList();
        if(outstanding.Any(x=>x.State==nameof(LocalSyncState.Conflict)))throw new InvalidOperationException("Resolve the conflicting assignment with the Job Owner before submitting dependent reports.");
        LocalAssignment current=await store.GetAssignmentAsync(assignment.Id)??assignment;
        if(!current.IsCurrent)throw new InvalidOperationException("Refresh Studio assignments. This session is no longer in your current assignments; its old reports remain available for review.");
        long expected=WorkDayClientRules.NextExpectedVersionAfterPending(current.Version,
            outstanding.Where(x=>WorkDayClientRules.ChangesAssignmentStatus(x.Kind)&&x.ExpectedVersion.HasValue).Select(x=>x.ExpectedVersion!.Value));
        await store.QueueAsync(new LocalOperation{OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=assignment.Id,
            OccurredAtUtc=DateTimeOffset.UtcNow,DeviceId=await device.GetAsync(),Text=text,Category=category,
            ExpectedVersion=expected,State=nameof(LocalSyncState.Pending)});
        await RefreshStatusAsync();
        try{await sync.SynchronizeAsync();}catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException){}
        await RefreshStatusAsync();
    }

    private async Task NavigateAsync()
    {
        if(string.IsNullOrWhiteSpace(assignment.Location))throw new InvalidOperationException("No location is recorded.");
        await Launcher.Default.OpenAsync($"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(assignment.Location)}");
    }
    private async Task ContactAsync()
    {
        string[] choices=[..new[]{assignment.CustomerPhone,assignment.CustomerEmail}.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)];
        if(choices.Length==0)throw new InvalidOperationException("No permitted contact information is available.");
        string selected=await DisplayActionSheetAsync("Contact customer","Cancel",null,choices);
        if(selected=="Cancel"||string.IsNullOrWhiteSpace(selected))return;
        await Launcher.Default.OpenAsync($"{(selected.Contains('@')?"mailto":"tel")}:{Uri.EscapeDataString(selected)}");
    }
}
