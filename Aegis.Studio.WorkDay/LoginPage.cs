using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.WorkDay;

public sealed class LoginPage:ContentPage
{
    private readonly WorkDayApiClient api; private readonly WorkDaySessionContext session; private readonly IServiceProvider services; private readonly WorkDayDeviceIdentity device; private readonly WorkDayCredentialStore credentials;
    private readonly Entry user=new(){Placeholder="User name"};
    private readonly Entry password=new(){Placeholder="Password",IsPassword=true};
    private readonly Label status=new(){Text="Authenticate through Bryan’s API Site. Signing in for work happens separately."};
    public LoginPage(WorkDayApiClient api,WorkDaySessionContext session,IServiceProvider services,WorkDayDeviceIdentity device,WorkDayCredentialStore credentials)
    {
        this.api=api;this.session=session;this.services=services;this.device=device;this.credentials=credentials;Title="WorkDay Login";
        Button login=new(){Text="LOGIN",HeightRequest=58,FontSize=18};login.Clicked+=Login;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=24,Spacing=14,Children={new Label{Text="AEGIS STUDIO WORKDAY",FontSize=28,FontAttributes=FontAttributes.Bold},new Label{Text="Secure field access · connected through api.bryanstudiosltd.com"},user,password,login,status}}};
    }
    private async void Login(object? sender,EventArgs e)
    {
        if(sender is Button button)button.IsEnabled=false;
        try
        {
            status.Text="Authenticating…";
            Uri uri=WorkDayApiClient.ApiBaseUri;
            api.SetDeviceToken(null);api.SetServer(uri); await api.LoginAsync(user.Text??"",password.Text??"");
            WorkDayBootstrapDto? bootstrap=await api.BootstrapAsync();
            if(bootstrap is null) throw new InvalidOperationException("Studio did not return a WorkDay profile.");
            await services.GetRequiredService<WorkDayLocalStore>().BindAccountAsync(uri,bootstrap.UserName);
            string deviceId=await device.GetAsync();
            DeviceSessionDto issued=await api.IssueDeviceSessionAsync(deviceId);
            await credentials.SaveAsync(issued.Token,issued.ExpiresAtUtc);
            api.SetDeviceToken(issued.Token);
            Preferences.Default.Set("WorkDay.ServerUrl",uri.ToString());
            Preferences.Default.Set("WorkDay.UserName",bootstrap.UserName);
            Preferences.Default.Set("WorkDay.DisplayName",bootstrap.DisplayName);
            session.UserName=bootstrap.UserName;session.DisplayName=bootstrap.DisplayName;session.Server=uri;session.IsAuthenticated=true;
            password.Text="";
            MainPage main=services.GetRequiredService<MainPage>();
            Application.Current!.Windows[0].Page=new NavigationPage(main);
        }
        catch(Exception ex){status.Text="Login failed: "+ex.Message;}
        finally{if(sender is Button finished)finished.IsEnabled=true;}
    }
}
