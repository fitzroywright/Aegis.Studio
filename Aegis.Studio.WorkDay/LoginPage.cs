namespace Aegis.Studio.WorkDay;

public sealed class LoginPage:ContentPage
{
    private readonly WorkDayApiClient api; private readonly WorkDaySessionContext session; private readonly IServiceProvider services;
    private readonly Entry server=new(){Placeholder="https://studio.example.com",Keyboard=Keyboard.Url};
    private readonly Entry user=new(){Placeholder="User name"};
    private readonly Entry password=new(){Placeholder="Password",IsPassword=true};
    private readonly Label status=new(){Text="Authenticate to Aegis Studio. Signing in for work happens separately."};
    public LoginPage(WorkDayApiClient api,WorkDaySessionContext session,IServiceProvider services)
    {
        this.api=api;this.session=session;this.services=services;Title="WorkDay Login";
        server.Text=Preferences.Default.Get("WorkDay.ServerUrl","");
        Button login=new(){Text="LOGIN",HeightRequest=58,FontSize=18};login.Clicked+=Login;
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=24,Spacing=14,Children={new Label{Text="AEGIS STUDIO WORKDAY",FontSize=28,FontAttributes=FontAttributes.Bold},new Label{Text="Secure field access"},server,user,password,login,status}}};
    }
    private async void Login(object? sender,EventArgs e)
    {
        try
        {
            status.Text="Authenticating…";
            if(!Uri.TryCreate(server.Text?.Trim().TrimEnd('/')+"/",UriKind.Absolute,out Uri? uri) || uri.Scheme!="https")
                throw new InvalidOperationException("Enter the HTTPS Studio service URL.");
            api.SetServer(uri); await api.LoginAsync(user.Text??"",password.Text??"");
            WorkDayBootstrapDto? bootstrap=await api.BootstrapAsync();
            if(bootstrap is null) throw new InvalidOperationException("Studio did not return a WorkDay profile.");
            Preferences.Default.Set("WorkDay.ServerUrl",uri.ToString());
            session.UserName=bootstrap.UserName;session.DisplayName=bootstrap.DisplayName;session.Server=uri;session.IsAuthenticated=true;
            password.Text="";
            MainPage main=services.GetRequiredService<MainPage>();
            Application.Current!.Windows[0].Page=new NavigationPage(main);
        }
        catch(Exception ex){status.Text="Login failed: "+ex.Message;}
    }
}
