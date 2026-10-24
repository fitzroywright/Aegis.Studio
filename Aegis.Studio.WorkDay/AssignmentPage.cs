using Aegis.Studio.WorkDay.Core;

namespace Aegis.Studio.WorkDay;

public sealed class AssignmentPage:ContentPage
{
    private readonly LocalAssignment assignment;
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly WorkDayDeviceIdentity device;
    private readonly WorkDayApiClient api;
    private readonly WorkDayUploadService uploads;
    private readonly Label status=new();
    private readonly Label roster=new();
    private bool reporting;
    private readonly Label syncStatus=new();
    private bool observing;

    public AssignmentPage(LocalAssignment assignment,WorkDayLocalStore store,WorkDaySyncEngine sync,WorkDayDeviceIdentity device,WorkDayApiClient api,WorkDayUploadService uploads)
    {
        this.assignment=assignment;this.store=store;this.sync=sync;this.device=device;this.api=api;this.uploads=uploads;Title=assignment.Title;
        Button communication=Action("CLIENT COMMUNICATION",async()=>
        {
            if(assignment.ConversationId is Guid conversationId)
                await Navigation.PushAsync(new WorkDayConversationPage(api,store,sync,conversationId,assignment.ClientName??"Client communication"));
        });
        communication.IsVisible=assignment.ConversationId.HasValue;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={
            new Label{Text=assignment.Title,FontSize=25,FontAttributes=FontAttributes.Bold},
            new Label{Text=assignment.ClientName??""},
            new Label{Text=$"{assignment.StartsAtUtc.ToLocalTime():f} — {assignment.EndsAtUtc.ToLocalTime():t}"},
            new Label{Text=assignment.Location??"Location not set"},new Label{Text=assignment.Instructions??"No special instructions."},status,syncStatus,
            Action("SYNC NOW",async()=>{await sync.SynchronizeAsync();await RefreshStatusAsync();}),
            Action("SESSION / COLLECTION / ROSTER STATUS",ReportFieldStatusAsync),
            communication,
            Action("REVIEW REPORTS / RESOLVE CONFLICT",()=>Navigation.PushAsync(new WorkDayRecoveryPage(assignment.Id,store,sync))),
            Action("ACCEPT ASSIGNMENT",()=>ReportAsync("AcceptAssignment")),
            Action("DECLINE ASSIGNMENT",async()=>{string? reason=await DisplayPromptAsync("Decline assignment","Reason (required)");if(!string.IsNullOrWhiteSpace(reason))await ReportAsync("DeclineAssignment",reason);}),
            Action("ARRIVED",()=>ReportAsync("Arrive")),Action("START SESSION",()=>ReportAsync("StartSession")),
            Action("COMPLETE SESSION",()=>ReportAsync("CompleteSession")),
            Action("ADD NOTE",async()=>{string? text=await DisplayPromptAsync("Operational note","Note for the Job Owner and Workspace Manager");if(!string.IsNullOrWhiteSpace(text))await ReportAsync("AddNote",text);}),
            Action("REPORT A PROBLEM",async()=>{string? text=await DisplayPromptAsync("Report a problem","Describe the problem");if(!string.IsNullOrWhiteSpace(text))await ReportAsync("RecordException",text,"Other");}),
            Action("NAVIGATE",NavigateAsync),Action("CONTACT",ContactAsync),
            Action("UPLOAD FILE",async()=>
            {
                LocalAssignment cached=await store.GetAssignmentAsync(assignment.Id)??assignment;
                var destination=System.Text.Json.JsonSerializer.Deserialize<RemoteMediaDestinationDto>(cached.MediaDestinationJson??"null",new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                if(destination is null)throw new InvalidOperationException("Synchronize this assignment once to obtain authorized media destinations.");
                string collection=await DisplayActionSheetAsync("Upload collection","Cancel",null,destination.Collections.ToArray());
                if(collection=="Cancel"||string.IsNullOrWhiteSpace(collection))return;
                var subjects=destination.Subjects.Where(x=>x.Collection==collection).ToArray();Guid? subjectId=null;
                if(subjects.Length>0)
                {
                    var labels=subjects.Select(x=>x.Name+" · "+x.Id.ToString("D")).ToArray();
                    string chosen=await DisplayActionSheetAsync("Upload subject","Cancel",null,new[]{"No specific subject"}.Concat(labels).ToArray());
                    if(chosen=="Cancel"||string.IsNullOrWhiteSpace(chosen))return;
                    int index=Array.IndexOf(labels,chosen);if(index>=0)subjectId=subjects[index].Id;
                }
                LocalUpload? upload=await uploads.PickAndQueueAsync(assignment.Id,new(destination.JobId,destination.SessionId,collection,subjectId));
                if(upload is null)return;
                try
                {
                    await sync.SynchronizeAsync();
                    await uploads.ProcessAsync();
                    var currentUpload=await store.GetUploadAsync(upload.UploadId);
                    await DisplayAlertAsync("Upload",currentUpload?.State==nameof(LocalUploadState.Uploaded)?"Imported into Studio.":"Upload retained until Studio confirms import. See synchronization status.","OK");
                }
                catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    await DisplayAlertAsync("Saved offline","The file is queued on this device and will sync when connectivity returns. "+ex.Message,"OK");
                }
            }),
            new Label{Text="Roster and collections",FontAttributes=FontAttributes.Bold},roster,
            new Label{Text="Use Complete Session to finish actual attendance after recording the roster. The Job Owner can schedule the remainder. Upload photographs in Studio after the session."}
        }}};
    }

    protected override async void OnAppearing(){base.OnAppearing();if(!observing){sync.Changed+=SyncChanged;observing=true;}await sync.LoadStatusAsync();await RefreshStatusAsync();}
    protected override void OnDisappearing(){if(observing){sync.Changed-=SyncChanged;observing=false;}base.OnDisappearing();}
    private void SyncChanged()=>MainThread.BeginInvokeOnMainThread(async()=>{try{await RefreshStatusAsync();}catch(Exception ex){syncStatus.Text=ex.Message;}});
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
        if(current is not null){assignment.Version=current.Version;assignment.Status=current.Status;assignment.IsCurrent=current.IsCurrent;assignment.FieldTargetsJson=current.FieldTargetsJson;}
        var targets=System.Text.Json.JsonSerializer.Deserialize<List<FieldTargetDto>>(assignment.FieldTargetsJson??"[]")??[];
        roster.Text=string.Join("\n\n",targets.Where(x=>x.Scope=="Roster").Select(x=>$"{x.Name} · {x.Reference} · {x.Collection} · {x.Status??"Not Photographed"}\n{x.Instructions}"));
        List<LocalOperation> pending=(await store.UnacknowledgedAsync()).Where(x=>x.AssignmentId==assignment.Id).ToList();
        string projected=assignment.Status;
        foreach(LocalOperation op in pending)projected=WorkDayClientRules.AssignmentStatus(op.Kind,op.Category,projected);
        syncStatus.Text=sync.State=="Sending"?$"Synchronizing {sync.CompletedUpdates} / {sync.TotalUpdates}":
            $"Sync: {sync.State} · Last successful sync: {(sync.LastSuccessUtc is null?"Never":sync.LastSuccessUtc.Value.ToLocalTime().ToString("g"))}"+(sync.LastError is null?"":"\n"+sync.LastError);
        status.Text=(assignment.IsCurrent?"":"Historical / no longer in current assignments · ")+$"Studio: {assignment.Status}"+(pending.Count==0?"":$" · Local: {projected} · {pending.Count} report(s) unacknowledged")+string.Concat(pending.Where(x=>x.Kind=="RecordFieldStatus").Select(x=>$"\nLocal {x.State}: {x.Text}"));
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

    private async Task ReportFieldStatusAsync()
    {
        var current=await store.GetAssignmentAsync(assignment.Id)??assignment;
        var targets=System.Text.Json.JsonSerializer.Deserialize<List<FieldTargetDto>>(current.FieldTargetsJson??"[]")??[];
        if(targets.Count==0)throw new InvalidOperationException("Sync this assignment with the updated Studio server to cache its session, collections and roster before reporting offline.");
        if(targets.Count>30)
        {
            string? search=await DisplayPromptAsync("Find roster target","Name, reference or collection (leave empty for all)");
            if(search is null)return;
            if(!string.IsNullOrWhiteSpace(search))targets=targets.Where(x=>$"{x.Name} {x.Reference} {x.Collection}".Contains(search,StringComparison.OrdinalIgnoreCase)).ToList();
            if(targets.Count==0)throw new InvalidOperationException("No cached target matches that search.");
        }
        string[] choices=targets.Select((x,i)=>$"{i+1}. {x.Scope}: {x.Name} · {x.Status??"NotStarted"}").ToArray();
        string chosen=await DisplayActionSheetAsync("Choose reporting target","Cancel",null,choices);
        int index=Array.IndexOf(choices,chosen);if(index<0)return;
        var target=targets[index];
        string state=await DisplayActionSheetAsync("Photography status","Cancel",null,"NotStarted","InProgress","Photographed","Absent","Deferred","Cancelled");
        if(state=="Cancel"||string.IsNullOrWhiteSpace(state))return;
        string? note=await DisplayPromptAsync("Field update",state is "Cancelled" or "Deferred" or "Absent"?"Reason (required)":"Optional operational note");
        if(note is null)return;
        if((state is "Cancelled" or "Deferred" or "Absent")&&string.IsNullOrWhiteSpace(note))throw new InvalidOperationException("Enter a reason for this status.");
        long expected=target.Version;
        foreach(var op in (await store.UnacknowledgedAsync()).Where(x=>x.AssignmentId==assignment.Id&&x.Kind=="RecordFieldStatus"))
        {
            var prior=System.Text.Json.JsonSerializer.Deserialize<FieldStatusDto>(op.Category??"null");
            if(prior?.TargetId==target.TargetId)expected=Math.Max(expected,checked(prior.ExpectedTargetVersion+1));
        }
        await ReportAsync("RecordFieldStatus",$"{target.Scope}: {target.Name} — {state}. {note}",
            System.Text.Json.JsonSerializer.Serialize(new FieldStatusDto(target.TargetId,state,target.SourceFingerprint,expected,note)));
    }

    private async Task NavigateAsync()
    {
        if(string.IsNullOrWhiteSpace(assignment.Location))throw new InvalidOperationException("No location is recorded.");
        await Launcher.Default.OpenAsync($"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(assignment.Location)}");
    }
    private async Task ContactAsync()
    {
        string[] choices=[..new[]{assignment.ClientPhone,assignment.ClientEmail}.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)];
        if(choices.Length==0)throw new InvalidOperationException("No permitted contact information is available.");
        string selected=await DisplayActionSheetAsync("Contact client","Cancel",null,choices);
        if(selected=="Cancel"||string.IsNullOrWhiteSpace(selected))return;
        await Launcher.Default.OpenAsync($"{(selected.Contains('@')?"mailto":"tel")}:{Uri.EscapeDataString(selected)}");
    }
}


