namespace Aegis.Studio.Photographer;

public sealed class App : Application
{
    public App(MainPage mainPage)
    {
        MainPage = new NavigationPage(mainPage);
    }
}
