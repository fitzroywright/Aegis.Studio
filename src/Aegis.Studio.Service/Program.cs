using Aegis.Studio.Application.Bookings;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Application.Operations;
using Aegis.Studio.Infrastructure;
using Aegis.Studio.Infrastructure.Persistence;
using Aegis.Studio.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<BookingManagementService>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddScoped<EscalationService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHostedService<InboundMailWorker>();
builder.Services.AddHostedService<EscalationWorker>();

IHost host = builder.Build();

using (IServiceScope scope = host.Services.CreateScope())
{
    DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

await host.RunAsync();
