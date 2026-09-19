namespace Aegis.Studio.WorkDay;
public sealed class App:Application
{
    private readonly LoginPage login;
    public App(LoginPage login){this.login=login;}
    protected override Window CreateWindow(IActivationState? activationState)=>new(new NavigationPage(login));
}
