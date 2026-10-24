namespace Aegis.Studio.WorkDay;

public sealed class WorkDayMessagesPage:ContentPage
{
    private readonly WorkDayApiClient api;
    private readonly WorkDayLocalStore store;
    private readonly WorkDaySyncEngine sync;
    private readonly VerticalStackLayout list=new(){Spacing=10};
    public WorkDayMessagesPage(WorkDayApiClient api,WorkDayLocalStore store,WorkDaySyncEngine sync)
    {
        this.api=api;this.store=store;this.sync=sync;Title="Messages";
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={new Label{Text="MESSAGES",FontSize=24,FontAttributes=FontAttributes.Bold},new Label{Text="Messages sent offline are encrypted on this device and sync when the API connection returns."},list}}};
        Loaded+=async(_,_)=>await LoadAsync();
    }
    private async Task LoadAsync()
    {
        list.Children.Clear();
        IReadOnlyList<MessageThreadDto> threads;
        try
        {
            threads=await api.GetThreadsAsync()??[];
            await store.ReplaceCachedThreadsAsync(threads);
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {
            threads=await store.GetCachedThreadsAsync();
            list.Children.Add(new Label{Text="Offline · showing the last messages cached on this device."});
        }
        foreach(MessageThreadDto t in threads)
        {
            Button b=new(){Text=$"{t.Title}\n{t.UnreadCount} unread",HeightRequest=68};
            b.Clicked+=async(_,_)=>await Navigation.PushAsync(new WorkDayThreadPage(api,store,sync,t));
            list.Children.Add(b);
        }
        if(threads.Count==0)list.Children.Add(new Label{Text="No cached message threads."});
        foreach(LocalMessageSubmission pending in await store.UnacknowledgedMessagesAsync())
            list.Children.Add(new Label{Text=$"Waiting to sync · {pending.TargetType} · {pending.CreatedAtUtc.ToLocalTime():g} · {pending.Body}"});
    }
}

public sealed class WorkDayThreadPage:ContentPage
{
    private readonly WorkDayApiClient api;private readonly WorkDayLocalStore store;private readonly WorkDaySyncEngine sync;private readonly MessageThreadDto thread;
    private readonly VerticalStackLayout messages=new(){Spacing=8};private readonly Editor compose=new(){Placeholder="Message",AutoSize=EditorAutoSizeOption.TextChanges};
    public WorkDayThreadPage(WorkDayApiClient api,WorkDayLocalStore store,WorkDaySyncEngine sync,MessageThreadDto thread)
    {
        this.api=api;this.store=store;this.sync=sync;this.thread=thread;Title=thread.Title;
        Button send=new(){Text="SEND",HeightRequest=52};send.Clicked+=Send;
        ScrollView messageScroll=new(){Content=messages};
        VerticalStackLayout composer=new(){Padding=12,Children={compose,send}};
        Grid layout=new(){RowDefinitions={new RowDefinition(GridLength.Star),new RowDefinition(GridLength.Auto)}};
        layout.Children.Add(messageScroll);
        layout.Children.Add(composer);
        Grid.SetRow(composer,1);
        Content=layout;
        Loaded+=async(_,_)=>await LoadAsync();
    }
    private async Task LoadAsync()
    {
        messages.Children.Clear();
        try
        {
            IReadOnlyList<MessageDto> rows=await api.GetMessagesAsync(thread.Id)??[];
            await store.ReplaceCachedThreadMessagesAsync(thread.Id,rows);
            foreach(MessageDto m in rows)messages.Children.Add(new Border{Padding=10,Content=new VerticalStackLayout{Children={new Label{Text=m.SenderUserName,FontAttributes=FontAttributes.Bold},new Label{Text=m.Body},new Label{Text=m.CreatedAtUtc.ToLocalTime().ToString("g"),FontSize=11}}}});
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException)
        {
            messages.Children.Add(new Label{Text="Offline · showing messages last cached on this device."});
            IReadOnlyList<MessageDto> rows=await store.GetCachedThreadMessagesAsync(thread.Id);
            foreach(MessageDto m in rows)messages.Children.Add(new Border{Padding=10,Content=new VerticalStackLayout{Children={new Label{Text=m.SenderUserName,FontAttributes=FontAttributes.Bold},new Label{Text=m.Body},new Label{Text=m.CreatedAtUtc.ToLocalTime().ToString("g"),FontSize=11}}}});
        }
        foreach(LocalMessageSubmission pending in (await store.UnacknowledgedMessagesAsync()).Where(x=>x.TargetType=="Thread"&&x.TargetId==thread.Id))
            messages.Children.Add(new Border{Padding=10,Content=new VerticalStackLayout{Children={new Label{Text="Saved on this device · waiting to sync",FontAttributes=FontAttributes.Bold},new Label{Text=pending.Body}}}});
    }
    private async void Send(object? s,EventArgs e)
    {
        string body=compose.Text?.Trim()??"";if(body.Length==0)return;
        try
        {
            await store.QueueMessageAsync(new LocalMessageSubmission{OperationId=Guid.NewGuid(),TargetId=thread.Id,TargetType="Thread",Body=body,CreatedAtUtc=DateTimeOffset.UtcNow});
            compose.Text="";
            try{await sync.SynchronizeAsync();}catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException){}
            await LoadAsync();
        }
        catch(Exception ex){await DisplayAlertAsync("Message not saved","The message could not be written to this device. "+ex.Message,"OK");}
    }
}

public sealed class WorkDayConversationPage:ContentPage
{
    private readonly WorkDayApiClient api;private readonly WorkDayLocalStore store;private readonly WorkDaySyncEngine sync;private readonly Guid conversationId;
    private readonly Picker identity=new(){Title="Sending identity"};
    private readonly Editor body=new(){Placeholder="Reply to client",AutoSize=EditorAutoSizeOption.TextChanges};
    private readonly Label identityState=new(){Text="Workspace is the safe default."};
    private List<SendingIdentityOptionDto> options=[];

    public WorkDayConversationPage(WorkDayApiClient api,WorkDayLocalStore store,WorkDaySyncEngine sync,Guid conversationId,string title)
    {
        this.api=api;this.store=store;this.sync=sync;this.conversationId=conversationId;Title=title;
        Button send=new(){Text="SEND REPLY",HeightRequest=56};send.Clicked+=Send;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={new Label{Text="Client COMMUNICATION",FontSize=22,FontAttributes=FontAttributes.Bold},identity,identityState,body,send}}};
        identity.SelectedIndexChanged+=(_,_)=>UpdateIdentityLabel();
        Loaded+=async(_,_)=>await LoadIdentities();
    }

    private async Task LoadIdentities()
    {
        try
        {
            SendingIdentityOptionsDto? result=await api.GetSendingIdentitiesAsync(conversationId);
            options=result?.Identities?.ToList()??[];
            identity.ItemsSource=options.Select(x=>x.Label).ToList();
            int workspace=options.FindIndex(x=>x.Type.Equals("Workspace",StringComparison.OrdinalIgnoreCase));
            identity.SelectedIndex=workspace>=0?workspace:0;
            UpdateIdentityLabel();
        }
        catch(Exception ex){identityState.Text="Sending identities unavailable: "+ex.Message;}
    }

    private void UpdateIdentityLabel()
    {
        if(identity.SelectedIndex<0||identity.SelectedIndex>=options.Count){identityState.Text="Select a sending identity.";return;}
        SendingIdentityOptionDto selected=options[identity.SelectedIndex];
        identityState.Text=selected.Type=="Self"
            ? $"REPLY AS MYSELF · From: {selected.FromAddress} · Your identity will be audited."
            : $"WORKSPACE · From: {selected.FromAddress} · Safe default";
    }

    private async void Send(object? sender,EventArgs e)
    {
        string message=body.Text?.Trim()??"";
        if(message.Length==0)return;
        if(identity.SelectedIndex<0||identity.SelectedIndex>=options.Count){await DisplayAlertAsync("Identity required","Select the sending identity before sending.","OK");return;}
        SendingIdentityOptionDto selected=options[identity.SelectedIndex];
        bool confirm=await DisplayAlertAsync("Confirm sender",$"Send this reply as {selected.Label}?","SEND","CANCEL");
        if(!confirm)return;
        try
        {
            await store.QueueMessageAsync(new LocalMessageSubmission{OperationId=Guid.NewGuid(),TargetId=conversationId,TargetType="Conversation",Body=message,SendingIdentity=selected.Type,CreatedAtUtc=DateTimeOffset.UtcNow});
            body.Text="";
            try
            {
                await sync.SynchronizeAsync();
                await DisplayAlertAsync("Sent","Studio acknowledged the reply for delivery.","OK");
            }
            catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                await DisplayAlertAsync("Saved offline","The reply is encrypted on this device and will sync when the API connection returns. "+ex.Message,"OK");
            }
        }
        catch(Exception ex){await DisplayAlertAsync("Not saved","The reply could not be saved to this device. "+ex.Message,"OK");}
    }
}

