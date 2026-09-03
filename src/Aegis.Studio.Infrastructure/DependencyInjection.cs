using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Infrastructure.Integrations;
using Aegis.Studio.Infrastructure.Mail;
using Aegis.Studio.Infrastructure.Notifications;
using Aegis.Studio.Infrastructure.Persistence;
using Aegis.Studio.Infrastructure.Storage;
using Common.Security.Abstractions;
using Common.Security.Authenticators;
using Common.Security.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStudioInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);
        AddAuthentication(services, configuration);
        services.AddScoped<StudioRepository>();
        services.AddScoped<IBookingRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IStudioCatalogRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<ICommunicationRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IWorkspaceRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IOperationsRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<StudioDbContext>());
        services.AddSingleton<IMediaStorage, LocalMediaStorage>();
        services.AddSingleton<IOutboundMailProvider, LoggingOutboundMailProvider>();
        services.AddSingleton<IInboundMailProvider, NoOpInboundMailProvider>();
        services.AddSingleton<ICalendarProvider, DisabledCalendarProvider>();
        services.AddSingleton<ITaskSyncProvider, DisabledTaskSyncProvider>();
        services.AddSingleton<IContactSyncProvider, DisabledContactSyncProvider>();
        services.AddSingleton<INotificationChannel, DisabledNotificationChannel>();
        return services;
    }

    private static void AddAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Authentication:ActiveDirectory");
        string domain = section["Domain"] ?? throw new InvalidOperationException("Authentication:ActiveDirectory:Domain is required.");
        string searchBase = section["SearchBase"] ?? BuildSearchBase(domain);
        string[] servers = GetServers(section);
        int port = int.TryParse(section["Port"], out int configuredPort) ? configuredPort : 636;
        int timeoutSeconds = int.TryParse(section["TimeoutSeconds"], out int configuredTimeout) ? configuredTimeout : 5;
        string[] administrativeGroups = section.GetSection("AdministrativeGroups").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        ActiveDirectoryAuthenticationOptions options = new()
        {
            Domain = domain,
            SearchBase = searchBase,
            Port = port,
            TimeoutSeconds = timeoutSeconds,
            Servers = servers,
            AdministrativeGroups = administrativeGroups.Length == 0
                ? ["Domain Admins", "Administrators", "Enterprise Admins", "Schema Admins"]
                : administrativeGroups
        };

        services.AddSingleton<IAuthenticator>(new ActiveDirectory(options));
    }

    private static string[] GetServers(IConfigurationSection section)
    {
        string[] servers = section.GetSection("Servers").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        if (servers.Length > 0)
        {
            return servers;
        }

        string? server = section["Server"];
        if (!string.IsNullOrWhiteSpace(server))
        {
            return [server];
        }

        throw new InvalidOperationException("At least one Authentication:ActiveDirectory server is required.");
    }

    private static string BuildSearchBase(string domain)
    {
        string[] labels = domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length == 0)
        {
            throw new InvalidOperationException("Authentication:ActiveDirectory:Domain must be a valid DNS domain name.");
        }

        return string.Join(',', labels.Select(label => $"DC={label}"));
    }

    private static void AddDatabase(IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration["Database:Provider"] ?? "Sqlite";
        string connectionString = configuration.GetConnectionString("Studio") ?? GetDefaultSqliteConnectionString();
        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<StudioDbContext>(options => options.UseNpgsql(connectionString));
            return;
        }

        services.AddDbContext<StudioDbContext>(options => options.UseSqlite(connectionString));
    }

    private static string GetDefaultSqliteConnectionString()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string studioData = Path.Combine(localData, "Aegis.Studio");
        Directory.CreateDirectory(studioData);
        return $"Data Source={Path.Combine(studioData, "aegis-studio.db")}";
    }
}
