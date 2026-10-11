using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.WorkDay;

public sealed class StartupPage:ContentPage
{
    private readonly WorkDayCredentialStore credentials;private readonly WorkDayApiClient api;private readonly WorkDaySessionContext session;private readonly IServiceProvider services;
    public StartupPage(WorkDayCredentialStore credentials,WorkDayApiClient api,WorkDaySessionContext session,IServiceProvider services)
    {
        this.credentials=credentials;this.api=api;this.session=session;this.services=services;
        Content=new Grid{Children={new VerticalStackLayout{VerticalOptions=LayoutOptions.Center,HorizontalOptions=LayoutOptions.Center,Spacing=12,Children={new ActivityIndicator{IsRunning=true},new Label{Text="Opening Aegis Studio WorkDay…"}}}}};
        Loaded+=async(_,_)=>await StartAsync();
    }
    private async Task StartAsync()
    {
        string url=Preferences.Default.Get("WorkDay.ServerUrl","");
        string user=Preferences.Default.Get("WorkDay.UserName","");
        string display=Preferences.Default.Get("WorkDay.DisplayName",user);
        var saved=await credentials.GetAsync();
        if(saved is null||!Uri.TryCreate(url,UriKind.Absolute,out Uri? server))
        {
            Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<LoginPage>());return;
        }
        api.SetServer(server);api.SetDeviceToken(saved.Value.Token);
        session.UserName=user;session.DisplayName=display;session.Server=server;session.IsAuthenticated=true;
        try
        {
            await services.GetRequiredService<WorkDayLocalStore>().BindAccountAsync(server,user);
            WorkDayBootstrapDto? remote=await api.BootstrapAsync();
            if(remote is not null){session.UserName=remote.UserName;session.DisplayName=remote.DisplayName;Preferences.Default.Set("WorkDay.UserName",remote.UserName);Preferences.Default.Set("WorkDay.DisplayName",remote.DisplayName);}
        }
        catch(HttpRequestException ex) when(ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            api.SetDeviceToken(null);session.IsAuthenticated=false;
            Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<LoginPage>());return;
        }
        catch(HttpRequestException){}
        catch(TaskCanceledException){}
        catch
        {
            credentials.Clear();Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<LoginPage>());return;
        }
        Application.Current!.Windows[0].Page=new NavigationPage(services.GetRequiredService<MainPage>());
    }
}
