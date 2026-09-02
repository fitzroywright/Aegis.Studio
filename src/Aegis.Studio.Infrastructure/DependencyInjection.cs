using Aegis.Studio.Application.Abstractions;
using Aegis.Studio.Infrastructure.Mail;
using Aegis.Studio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aegis.Studio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStudioInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddDatabase(services, configuration);
        services.AddScoped<StudioRepository>();
        services.AddScoped<IBookingRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IStudioCatalogRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<ICommunicationRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IWorkspaceRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IOperationsRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<StudioDbContext>());
        services.AddSingleton<IOutboundMailProvider, LoggingOutboundMailProvider>();
        services.AddSingleton<IInboundMailProvider, NoOpInboundMailProvider>();
        return services;
    }

    private static void AddDatabase(IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration["Database:Provider"] ?? "Sqlite";
        string connectionString = configuration.GetConnectionString("Studio") ?? "Data Source=aegis-studio.db";
        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContext<StudioDbContext>(options => options.UseNpgsql(connectionString));
            return;
        }
        services.AddDbContext<StudioDbContext>(options => options.UseSqlite(connectionString));
    }
}
