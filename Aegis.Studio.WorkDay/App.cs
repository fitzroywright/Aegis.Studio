namespace Aegis.Studio.WorkDay;
public sealed class App:Application
{
    private readonly MainPage page;
    public App(MainPage page){this.page=page;}
    protected override Window CreateWindow(IActivationState? activationState)=>new(new NavigationPage(page));
}
