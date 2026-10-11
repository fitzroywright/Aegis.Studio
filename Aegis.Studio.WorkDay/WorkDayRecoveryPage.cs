using Aegis.Studio.WorkDay.Core;
namespace Aegis.Studio.WorkDay;

public sealed class WorkDayRecoveryPage:ContentPage
{
    private readonly Guid assignmentId;
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly VerticalStackLayout reports=new(){Spacing=10};
    private readonly Label status=new();
    private bool busy;
    public WorkDayRecoveryPage(Guid assignmentId,WorkDayLocalStore store,WorkDaySyncEngine sync)
    {
        this.assignmentId=assignmentId;this.store=store;this.sync=sync;Title="Report history / recovery";
        var refresh=new Button{Text="REFRESH / RETRY ORIGINAL REPORTS"};refresh.Clicked+=async(_,_)=>await RunAsync(async()=>{await sync.SynchronizeAsync();await RenderAsync();});
        var resolve=new Button{Text="KEEP HISTORY AND USE STUDIO STATE"};resolve.Clicked+=async(_,_)=>await RunAsync(ResolveAsync);
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={
            new Label{Text="Review the conflicting reports with the Job Owner. Original reports, times and operation IDs stay in history. Recovery does not report them as completed or replay them against a new assignment."},
            status,refresh,resolve,reports
        }}};
    }
    protected override async void OnAppearing(){base.OnAppearing();await RunAsync(RenderAsync);}
    private async Task RunAsync(Func<Task> action)
    {
        if(busy)return;busy=true;
        try{await action();}catch(Exception ex){await DisplayAlertAsync("Report recovery",ex.Message,"OK");}finally{busy=false;}
    }
    private async Task RenderAsync()
    {
        var current=await store.GetAssignmentAsync(assignmentId);
        status.Text=current is null?"No current assignment cached.":$"Studio: {current.Status} · version {current.Version}"+(current.IsCurrent?"":" · no longer in current assignments");
        reports.Children.Clear();
        foreach(var op in await store.OperationHistoryAsync(assignmentId))
        {
            string? resolution=await store.ResolutionTextAsync(op);
            reports.Children.Add(new Border{Padding=10,StrokeThickness=1,Content=new VerticalStackLayout{Spacing=4,Children={
                new Label{Text=$"{op.Kind} · {op.State}",FontAttributes=FontAttributes.Bold},
                new Label{Text=$"{op.OccurredAtUtc.ToLocalTime():g} · expected version {op.ExpectedVersion}"},
                new Label{Text=op.Text??""},new Label{Text=op.LastError??""},
                new Label{Text=resolution is null?"":$"Resolved {op.ResolvedAtUtc?.ToLocalTime():g}: {resolution}"},
                new Label{Text=$"Operation: {op.OperationId:D}",FontSize=11}
            }}});
        }
        if(reports.Children.Count==0)reports.Children.Add(new Label{Text="No reports recorded on this device."});
    }
    private async Task ResolveAsync()
    {
        var outstanding=(await store.UnacknowledgedAsync()).Where(x=>x.AssignmentId==assignmentId).ToArray();
        if(!outstanding.Any(x=>x.State=="Conflict"))throw new InvalidOperationException("There is no rejected conflict to resolve. Retry reports with an uncertain server result first.");
        string? reason=await DisplayPromptAsync("Resolution","Record the agreement with the Job Owner. Required; do not erase or silently replay old reports.",maxLength:4000);
        if(string.IsNullOrWhiteSpace(reason))return;
        if(!await DisplayAlertAsync("Retain reports as resolved history",$"Keep {outstanding.Length} report(s) in history and use the freshly retrieved Studio assignment state? These reports will not be marked acknowledged or submitted again.","Keep history / resolve","Cancel"))return;
        await sync.ResolveAssignmentConflictAsync(assignmentId,reason);
        await RenderAsync();
    }
}
