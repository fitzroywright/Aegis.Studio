namespace Aegis.Studio.WorkDay;

public sealed class WorkDayMessagesPage:ContentPage
{
    private readonly WorkDayApiClient api;
    private readonly VerticalStackLayout list=new(){Spacing=10};
    public WorkDayMessagesPage(WorkDayApiClient api)
    {
        this.api=api;Title="Messages";
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=18,Spacing=12,Children={new Label{Text="MESSAGES",FontSize=24,FontAttributes=FontAttributes.Bold},new Label{Text="Relevant Studio conversations. Internet connection is required to refresh messages."},list}}};
        Loaded+=async(_,_)=>await LoadAsync();
    }
    private async Task LoadAsync()
    {
        list.Children.Clear();
        try
        {
            IReadOnlyList<MessageThreadDto> threads=await api.GetThreadsAsync()??[];
            foreach(MessageThreadDto t in threads)
            {
                Button b=new(){Text=$"{t.Title}\n{t.UnreadCount} unread",HeightRequest=68};
                b.Clicked+=async(_,_)=>await Navigation.PushAsync(new WorkDayThreadPage(api,t));
                list.Children.Add(b);
            }
            if(threads.Count==0)list.Children.Add(new Label{Text="No message threads."});
        }
        catch(Exception ex){list.Children.Add(new Label{Text="Messages unavailable: "+ex.Message});}
    }
}

public sealed class WorkDayThreadPage:ContentPage
{
    private readonly WorkDayApiClient api;private readonly MessageThreadDto thread;
    private readonly VerticalStackLayout messages=new(){Spacing=8};private readonly Editor compose=new(){Placeholder="Message",AutoSize=EditorAutoSizeOption.TextChanges};
    public WorkDayThreadPage(WorkDayApiClient api,MessageThreadDto thread)
    {
        this.api=api;this.thread=thread;Title=thread.Title;
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
            foreach(MessageDto m in rows)messages.Children.Add(new Border{Padding=10,Content=new VerticalStackLayout{Children={new Label{Text=m.SenderUserName,FontAttributes=FontAttributes.Bold},new Label{Text=m.Body},new Label{Text=m.CreatedAtUtc.ToLocalTime().ToString("g"),FontSize=11}}}});
        }
        catch(Exception ex){messages.Children.Add(new Label{Text="Unable to load messages: "+ex.Message});}
    }
    private async void Send(object? s,EventArgs e)
    {
        string body=compose.Text?.Trim()??"";if(body.Length==0)return;
        try{await api.SendThreadMessageAsync(thread.Id,body);compose.Text="";await LoadAsync();}
        catch(Exception ex){await DisplayAlertAsync("Message not sent","Your message was not acknowledged by Studio. "+ex.Message,"OK");}
    }
}

public sealed class WorkDayConversationPage:ContentPage
{
    private readonly WorkDayApiClient api;private readonly Guid conversationId;
    private readonly Picker identity=new(){Title="Sending identity"};
    private readonly Editor body=new(){Placeholder="Reply to client",AutoSize=EditorAutoSizeOption.TextChanges};
    private readonly Label identityState=new(){Text="Workspace is the safe default."};
    private List<SendingIdentityOptionDto> options=[];

    public WorkDayConversationPage(WorkDayApiClient api,Guid conversationId,string title)
    {
        this.api=api;this.conversationId=conversationId;Title=title;
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
            await api.ReplyConversationAsync(conversationId,message,selected.Type);
            body.Text="";
            await DisplayAlertAsync("Sent","Studio accepted the reply for delivery.","OK");
        }
        catch(Exception ex){await DisplayAlertAsync("Not sent","Studio did not acknowledge the reply. "+ex.Message,"OK");}
    }
}

