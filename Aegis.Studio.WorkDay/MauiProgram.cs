namespace Aegis.Studio.WorkDay;
public static class MauiProgram
{
 public static MauiApp CreateMauiApp()
 {
   var b=MauiApp.CreateBuilder();
   b.UseMauiApp<App>();
   b.Services.AddSingleton<WorkDaySessionContext>();
   b.Services.AddSingleton<WorkDayCredentialStore>();
   b.Services.AddSingleton<WorkDayDataProtector>();
   b.Services.AddSingleton<WorkDayDeviceIdentity>();
   b.Services.AddSingleton<WorkDayLocalStore>();
   b.Services.AddSingleton<WorkDayApiClient>();
   b.Services.AddSingleton<WorkDaySyncEngine>();
   b.Services.AddSingleton<WorkDayAutoSync>();
   b.Services.AddSingleton<WorkDayLocationService>();
   b.Services.AddSingleton<WorkDayUploadService>();
   b.Services.AddTransient<StartupPage>();
   b.Services.AddTransient<LoginPage>();
   b.Services.AddTransient<MainPage>();
   return b.Build();
 }
}
