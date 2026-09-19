namespace Aegis.Studio.WorkDay;
public sealed class App:Application
{
    private readonly StartupPage startup;
    private readonly WorkDayLocationService location;
    public App(StartupPage startup,WorkDayLocationService location){this.startup=startup;this.location=location;}
    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window=new(new NavigationPage(startup));
        window.Stopped+=(_,_)=>location.NotifyAppSuspended();
        window.Resumed+=(_,_)=>location.NotifyAppResumed();
        return window;
    }
}
