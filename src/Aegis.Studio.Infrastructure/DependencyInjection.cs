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
        string connectionString = GetConnectionString(configuration);
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

    private static string GetConnectionString(IConfiguration configuration)
    {
        string? configuredConnectionString = configuration.GetConnectionString("Studio");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        string host = configuration["Database:Host"] ?? "axios";
        string database = configuration["Database:Name"] ?? "AegisStudio";
        string? username = configuration["Database:Username"] ?? Environment.GetEnvironmentVariable("AEGIS_PG_USER");
        string? password = configuration["Database:Password"] ?? Environment.GetEnvironmentVariable("AEGIS_PG_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("PostgreSQL credentials are not configured. Set ConnectionStrings:Studio or AEGIS_PG_USER and AEGIS_PG_PASSWORD.");
        }

        Npgsql.NpgsqlConnectionStringBuilder builder = new()
        {
            Host = host,
            Port = 5432,
            Database = database,
            Username = username,
            Password = password,
            PersistSecurityInfo = true
        };

        return builder.ConnectionString;
    }
}
