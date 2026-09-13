namespace Aegis.Studio.Photographer;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddSingleton<StudioApiClient>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
