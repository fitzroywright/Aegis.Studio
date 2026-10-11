namespace Aegis.Studio.WorkDay;
public sealed class App:Application
{
    private readonly StartupPage startup;
    private readonly WorkDayLocationService location;
    private readonly WorkDayAutoSync autoSync;
    public App(StartupPage startup,WorkDayLocationService location,WorkDayAutoSync autoSync)
    {
        this.startup=startup;this.location=location;this.autoSync=autoSync;
        Resources=WorkDayTheme.Create();
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        Window window=new(new NavigationPage(startup){BarBackgroundColor=Color.FromArgb("#29251E"),BarTextColor=Colors.White});
        window.Stopped+=(_,_)=>{location.NotifyAppSuspended();autoSync.Suspend();};
        window.Activated+=(_,_)=>autoSync.Resume();
        window.Destroying+=(_,_)=>autoSync.Suspend();
        window.Resumed+=(_,_)=>{location.NotifyAppResumed();autoSync.Resume();};
        return window;
    }
}
