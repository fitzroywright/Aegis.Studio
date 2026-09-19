namespace Aegis.Studio.WorkDay;
public sealed class App:Application
{
    private readonly StartupPage startup;
    public App(StartupPage startup){this.startup=startup;}
    protected override Window CreateWindow(IActivationState? activationState)=>new(new NavigationPage(startup));
}
