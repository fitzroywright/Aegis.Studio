using Aegis.Studio.Application.Bookings;
using Aegis.Studio.Application.Communications;
using Aegis.Studio.Application.Customers;
using Aegis.Studio.Application.Notifications;
using Aegis.Studio.Application.Operations;
using Aegis.Studio.Application.Tasks;
using Aegis.Studio.Infrastructure;
using Aegis.Studio.Infrastructure.Persistence;
using Aegis.Studio.Service;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddStudioInfrastructure(builder.Configuration, includeStaffAuthentication: false);
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<BookingManagementService>();
builder.Services.AddScoped<BookingLifecycleService>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddScoped<InboundMailRouter>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<TaskService>();
builder.Services.AddScoped<NotificationScheduler>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<EscalationService>();
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddHostedService<InboundMailWorker>();
builder.Services.AddHostedService<EscalationWorker>();
builder.Services.AddHostedService<NotificationWorker>();

IHost host = builder.Build();

using (IServiceScope scope = host.Services.CreateScope())
{
    DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

await host.RunAsync();
