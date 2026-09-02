using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Infrastructure.Integrations;
using Aegis.Studio.Infrastructure.Mail;
using Aegis.Studio.Infrastructure.Notifications;
using Aegis.Studio.Infrastructure.Persistence;
using Aegis.Studio.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStudioInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration); services.AddScoped<StudioRepository>(); services.AddScoped<IBookingRepository>(p=>p.GetRequiredService<StudioRepository>()); services.AddScoped<IStudioCatalogRepository>(p=>p.GetRequiredService<StudioRepository>()); services.AddScoped<ICommunicationRepository>(p=>p.GetRequiredService<StudioRepository>()); services.AddScoped<IWorkspaceRepository>(p=>p.GetRequiredService<StudioRepository>()); services.AddScoped<IOperationsRepository>(p=>p.GetRequiredService<StudioRepository>()); services.AddScoped<ICustomerRepository,CustomerRepository>(); services.AddScoped<ITaskRepository,TaskRepository>(); services.AddScoped<INotificationRepository,NotificationRepository>(); services.AddScoped<IUnitOfWork>(p=>p.GetRequiredService<StudioDbContext>()); services.AddSingleton<IMediaStorage,LocalMediaStorage>(); services.AddSingleton<IOutboundMailProvider,LoggingOutboundMailProvider>(); services.AddSingleton<IInboundMailProvider,NoOpInboundMailProvider>(); services.AddSingleton<ICalendarProvider,DisabledCalendarProvider>(); services.AddSingleton<ITaskSyncProvider,DisabledTaskSyncProvider>(); services.AddSingleton<IContactSyncProvider,DisabledContactSyncProvider>(); services.AddSingleton<INotificationChannel,DisabledNotificationChannel>(); return services;
    }
    private static void AddDatabase(IServiceCollection services,IConfiguration configuration){string provider=configuration["Database:Provider"]??"Sqlite";string connectionString=configuration.GetConnectionString("Studio")??GetDefaultSqliteConnectionString();if(provider.Equals("PostgreSql",StringComparison.OrdinalIgnoreCase)){services.AddDbContext<StudioDbContext>(options=>options.UseNpgsql(connectionString));return;}services.AddDbContext<StudioDbContext>(options=>options.UseSqlite(connectionString));}
    private static string GetDefaultSqliteConnectionString(){string localData=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);string studioData=Path.Combine(localData,"Aegis.Studio");Directory.CreateDirectory(studioData);return $"Data Source={Path.Combine(studioData,"aegis-studio.db")}";}
}
