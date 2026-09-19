namespace Aegis.Studio.WorkDay;

public sealed class AssignmentPage:ContentPage
{
    private readonly LocalAssignment assignment;
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly WorkDayUploadService uploads;
    private readonly WorkDayLocationService location;
    private readonly WorkDayDeviceIdentity device;
    private readonly WorkDayApiClient api;
    private readonly IServiceProvider services;
    private readonly Label status=new();
    private readonly Label uploadState=new();

    public AssignmentPage(
        LocalAssignment assignment,
        WorkDayLocalStore store,
        WorkDaySyncEngine sync,
        WorkDayUploadService uploads,
        WorkDayLocationService location,
        WorkDayDeviceIdentity device,
        WorkDayApiClient api,
        IServiceProvider services)
    {
        this.assignment=assignment;this.store=store;this.sync=sync;this.uploads=uploads;this.location=location;this.device=device;this.api=api;this.services=services;
        Title=assignment.Title;
        status.Text=$"Status: {assignment.Status}";
        Button navigate=Action("NAVIGATE",Navigate);
        Button contact=Action("CONTACT",Contact);
        Button accept=Action("ACCEPT / CONFIRM",async()=>await RecordStatus("AcceptAssignment"));
        Button arrived=Action("ARRIVED",async()=>await RecordStatus("Arrive",captureLocation:true));
        Button start=Action("START SESSION",async()=>await RecordStatus("StartSession",captureLocation:true));
        Button note=Action("ADD NOTE",AddNote);
        Button exception=Action("RECORD EXCEPTION",RecordException);
        Button exceptionStatus=Action("SET EXCEPTION STATUS",SetExceptionStatus);
        Button upload=Action("ADD / UPLOAD FILES",Upload);
        Button messages=Action("MESSAGES",Messages);
        Button gallery=Action("GALLERY STATUS",Gallery);
        Button complete=Action("COMPLETE SESSION",async()=>await RecordStatus("CompleteSession",captureLocation:true));

        Content=new ScrollView
        {
            Content=new VerticalStackLayout
            {
                Padding=18,Spacing=12,
                Children=
                {
                    new Label{Text=assignment.CustomerName??assignment.Title,FontSize=26,FontAttributes=FontAttributes.Bold},
                    new Label{Text=assignment.Title,FontAttributes=FontAttributes.Bold},
                    new Label{Text=$"{assignment.StartsAtUtc.ToLocalTime():f} — {assignment.EndsAtUtc.ToLocalTime():t}"},
                    new Label{Text=assignment.Location??"Location not set"},
                    new Label{Text=assignment.Instructions??"No special instructions."},
                    status,uploadState,
                    navigate,contact,accept,arrived,start,note,exception,exceptionStatus,upload,messages,gallery,complete
                }
            }
        };
        Loaded+=async(_,_)=>await RefreshUploadState();
    }

    private static Button Action(string text,Func<Task> action)
    {
        Button b=new(){Text=text,HeightRequest=56,FontSize=17};
        b.Clicked+=async(_,_)=>await action();
        return b;
    }

    private async Task RecordStatus(string kind,bool captureLocation=false)
    {
        string deviceId=await device.GetAsync();
        LocalLocationObservation? loc=captureLocation?await location.CaptureAsync(assignment.Id,(await store.ActiveSessionAsync())?.Id):null;
        await store.QueueAsync(new LocalOperation
        {
            OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=assignment.Id,OccurredAtUtc=DateTimeOffset.UtcNow,
            DeviceId=deviceId,ExpectedVersion=assignment.Version,State=nameof(LocalSyncState.Pending),
            LocationObservationId=loc?.ObservationId,Latitude=loc?.Latitude,Longitude=loc?.Longitude,AccuracyMetres=loc?.AccuracyMetres,
            LocationObservedAtUtc=loc?.ObservedAtUtc,LocationSource=loc?.Source
        });
        status.Text=$"{kind} safely recorded locally · synchronization pending";
        try{await sync.SynchronizeAsync();LocalAssignment? updated=await store.GetAssignmentAsync(assignment.Id);if(updated is not null){assignment.Version=updated.Version;assignment.Status=updated.Status;status.Text=$"Status: {updated.Status}";}}catch{}
    }

    private async Task AddNote()
    {
        string? text=await DisplayPromptAsync("Session Note","Internal field note");
        if(string.IsNullOrWhiteSpace(text))return;
        await QueueText("AddNote",text,null);
    }

    private async Task RecordException()
    {
        string category=await DisplayActionSheetAsync("Exception category","Cancel",null,"CustomerLate","NoShow","PhotographerDelayed","LocationInaccessible","EquipmentIssue","WeatherIssue","SessionInterrupted","CustomerRequestedChange","Other");
        if(string.IsNullOrWhiteSpace(category)||category=="Cancel")return;
        string? text=await DisplayPromptAsync("Exception", "Describe what happened");
        if(string.IsNullOrWhiteSpace(text))return;
        await QueueText("RecordException",text,category);
    }

    private async Task SetExceptionStatus()
    {
        string value=await DisplayActionSheetAsync("Exception status","Cancel",null,"Cancelled","NoShow","Delayed","UnableToComplete");
        if(string.IsNullOrWhiteSpace(value)||value=="Cancel")return;
        string? reason=await DisplayPromptAsync(value,"Reason");
        if(string.IsNullOrWhiteSpace(reason))return;
        string deviceId=await device.GetAsync();
        await store.QueueAsync(new LocalOperation{OperationId=Guid.NewGuid(),Kind="SetExceptionStatus",AssignmentId=assignment.Id,OccurredAtUtc=DateTimeOffset.UtcNow,DeviceId=deviceId,Text=reason,Category=value,ExpectedVersion=assignment.Version,State=nameof(LocalSyncState.Pending)});
        status.Text=$"{value} safely recorded locally · synchronization pending";
        try{await sync.SynchronizeAsync();LocalAssignment? updated=await store.GetAssignmentAsync(assignment.Id);if(updated is not null){assignment.Version=updated.Version;assignment.Status=updated.Status;status.Text=$"Status: {updated.Status}";}}catch{}
    }

    private async Task QueueText(string kind,string text,string? category)
    {
        string deviceId=await device.GetAsync();
        await store.QueueAsync(new LocalOperation{OperationId=Guid.NewGuid(),Kind=kind,AssignmentId=assignment.Id,OccurredAtUtc=DateTimeOffset.UtcNow,DeviceId=deviceId,Text=text,Category=category,ExpectedVersion=assignment.Version,State=nameof(LocalSyncState.Pending)});
        status.Text=$"{kind} safely stored locally";
        try{await sync.SynchronizeAsync();}catch{}
    }

    private async Task Upload()
    {
        LocalUpload? item=await uploads.PickAndQueueAsync(assignment.Id);
        if(item is null)return;
        uploadState.Text=$"{item.FileName} safely stored on device · Pending upload";
        try{await uploads.ProcessAsync();}catch{}
        await RefreshUploadState();
    }

    private async Task RefreshUploadState()
    {
        int count=(await store.PendingUploadsAsync()).Count(x=>x.AssignmentId==assignment.Id);
        uploadState.Text=count==0?"No files waiting to upload.":$"{count} file(s) waiting to upload.";
    }

    private async Task Navigate()
    {
        if(string.IsNullOrWhiteSpace(assignment.Location)){await DisplayAlertAsync("Navigation","No location is recorded for this assignment.","OK");return;}
        try{await Launcher.Default.OpenAsync($"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(assignment.Location)}");}
        catch{await DisplayAlertAsync("Navigation",assignment.Location,"OK");}
    }

    private async Task Contact()
    {
        string[] choices=[..new[]{assignment.CustomerPhone,assignment.CustomerEmail}.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x!)];
        if(choices.Length==0){await DisplayAlertAsync("Contact","No customer contact information is available for this assignment.","OK");return;}
        string selected=await DisplayActionSheetAsync("Contact customer","Cancel",null,choices);
        if(selected=="Cancel"||string.IsNullOrWhiteSpace(selected))return;
        if(selected.Contains('@')) await Launcher.Default.OpenAsync($"mailto:{Uri.EscapeDataString(selected)}");
        else await Launcher.Default.OpenAsync($"tel:{Uri.EscapeDataString(selected)}");
    }

    private async Task Messages()
    {
        if(assignment.ConversationId is Guid conversation)
        {
            await Navigation.PushAsync(new WorkDayConversationPage(api,conversation,assignment.Title));
            return;
        }
        if(assignment.JobId is Guid job)
        {
            try
            {
                MessageThreadDto thread=await api.GetOrCreateThreadAsync("Job",job);
                await Navigation.PushAsync(new WorkDayThreadPage(api,thread));
                return;
            }
            catch(Exception ex){await DisplayAlertAsync("Messages","Studio messaging is unavailable: "+ex.Message,"OK");return;}
        }
        await DisplayAlertAsync("Messages","This assignment is not linked to a Studio conversation or job.","OK");
    }

    private async Task Gallery()
    {
        try
        {
            AssignmentGalleryStatusDto? gallery=await api.GetAssignmentGalleryStatusAsync(assignment.Id);
            if(gallery is null){await DisplayAlertAsync("Gallery","Gallery status is unavailable.","OK");return;}
            string state=!gallery.Exists?"No gallery":gallery.IsPublished?"Published":"Internal / not published";
            await DisplayAlertAsync("Gallery",$"{gallery.Title??"Workspace Gallery"}\nStatus: {state}\nVisible items: {gallery.VisibleItems}","OK");
        }
        catch(Exception ex){await DisplayAlertAsync("Gallery","Gallery status is unavailable: "+ex.Message,"OK");}
    }
}
