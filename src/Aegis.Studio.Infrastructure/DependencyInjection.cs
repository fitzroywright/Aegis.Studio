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
        string connectionString = configuration.GetConnectionString("Studio") ?? "Host=localhost;Port=5432;Database=aegis_studio;Username=aegis;Password=local-development-only";
        services.AddDbContext<StudioDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<StudioRepository>();
        services.AddScoped<IBookingRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IStudioCatalogRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<ICommunicationRepository>(provider => provider.GetRequiredService<StudioRepository>());
        services.AddScoped<IUnitOfWork>(provider => provider.GetRequiredService<StudioDbContext>());
        services.AddSingleton<IOutboundMailProvider, LoggingOutboundMailProvider>();
        services.AddSingleton<IInboundMailProvider, NoOpInboundMailProvider>();
        return services;
    }
}
