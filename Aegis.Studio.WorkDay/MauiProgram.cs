namespace Aegis.Studio.WorkDay;
public static class MauiProgram
{
 public static MauiApp CreateMauiApp(){var b=MauiApp.CreateBuilder();b.UseMauiApp<App>();b.Services.AddSingleton<WorkDayLocalStore>();b.Services.AddSingleton<WorkDayApiClient>();b.Services.AddSingleton<WorkDaySyncEngine>();b.Services.AddSingleton<MainPage>();return b.Build();}
}
