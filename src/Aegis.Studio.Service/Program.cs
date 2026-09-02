using Aegis.Studio.Application.Bookings;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Infrastructure;
using Aegis.Studio.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddStudioInfrastructure(builder.Configuration);
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddHostedService<InboundMailWorker>();
IHost host = builder.Build();
await host.RunAsync();
