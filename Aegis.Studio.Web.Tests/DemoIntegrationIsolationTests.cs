using Aegis.Studio.Web.Email;
using Aegis.Studio.Web.Intake;
using Aegis.Studio.Web.Operations;
using Aegis.Studio.Web.WorkDay;
using Common.Messaging.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Npgsql;

namespace Aegis.Studio.Web.Tests;

public sealed class DemoIntegrationIsolationTests
{
    [StudioPostgresFact]
    public async Task MaintenanceDatabaseIdentityUsesTheServerAddressWithoutAnInetNetworkMask()
    {
        string connection = System.Environment.GetEnvironmentVariable("STUDIO_TEST_POSTGRES")!;
        await using var source = NpgsqlDataSource.Create(connection);
        var identity = await StudioMaintenanceCommand.ReadDatabaseIdentityAsync(source);
        Assert.Equal(new NpgsqlConnectionStringBuilder(connection).Database, identity.Name);
        Assert.Equal("public", identity.Schema);
        Assert.True(System.Net.IPAddress.TryParse(identity.ServerAddress, out _));
        Assert.DoesNotContain('/', identity.ServerAddress);
        Assert.True(identity.Port > 0);
    }

    private static ConfigurationManager Configuration(bool isolation = true, bool confirmed = true)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DemoIntegrationIsolation.ConfigurationKey] = isolation.ToString(),
            ["MockAuthentication:Enabled"] = "true", ["MockAuthentication:IsolationConfirmed"] = confirmed.ToString(),
            ["MockAuthentication:Identities:0:LoginName"] = "listed", ["MockAuthentication:Identities:0:SubjectId"] = "stable",
            ["Messaging:MicrosoftGraph:Email:Enabled"] = "true", ["Messaging:MicrosoftGraph:Email:InboundEnabled"] = "true",
            ["Messaging:MicrosoftGraph:Email:Delegated:Enabled"] = "true", ["Messaging:Slack:Enabled"] = "true",
            ["PublicSite:PollingEnabled"] = "true", ["PublicSite:IntegrationToken"] = new string('x', 40),
            ["WorkDay:ApiSyncKey"] = "synthetic-retained-credential", ["Storage:Provider"] = "S3", ["Storage:S3:RootFolder"] = "unchanged-demo-root"
        });
        return config;
    }

    [Fact]
    public void DemoPausesEffectiveDispatchWithoutErasingCredentialsOrStorageConfiguration()
    {
        var configuration = Configuration();
        Assert.True(DemoIntegrationIsolation.Configure(configuration, new Environment("Demo")));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Enabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:InboundEnabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Delegated:Enabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:Slack:Enabled"));
        Assert.False(configuration.GetValue<bool>("PublicSite:PollingEnabled"));
        Assert.Equal("synthetic-retained-credential", configuration["WorkDay:ApiSyncKey"]);
        Assert.Equal(new string('x',40), configuration["PublicSite:IntegrationToken"]);
        Assert.Equal("S3", configuration["Storage:Provider"]); Assert.Equal("unchanged-demo-root", configuration["Storage:S3:RootFolder"]);
    }

    [Theory]
    [InlineData("Production", true, true)]
    [InlineData("Demo", false, true)]
    [InlineData("Demo", true, false)]
    [InlineData("Development", true, true)]
    public void InvalidModesFailBeforeExternalClientsAreRegistered(string environment, bool isolated, bool confirmed)
        => Assert.Throws<InvalidOperationException>(() => DemoIntegrationIsolation.Configure(Configuration(isolated, confirmed), new Environment(environment)));

    [Fact]
    public void NonMockProductionConfigurationIsUnchanged()
    {
        var configuration = Configuration(false); configuration["MockAuthentication:Enabled"] = "false";
        Assert.False(DemoIntegrationIsolation.Configure(configuration, new Environment("Production")));
        Assert.True(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Enabled"));
        Assert.Equal("synthetic-retained-credential", configuration["WorkDay:ApiSyncKey"]);
    }

    [Fact]
    public async Task RequestDrivenClientsFailBeforeTheirTransportRuns()
    {
        var transport = new RecordingHandler();
        using var client = new HttpClient(new DemoIntegrationIsolationHandler { InnerHandler = transport });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://external.example.test/private"));
        Assert.Equal(0, transport.Requests);
        Assert.Throws<InvalidOperationException>(() => new PublicSiteClient(Configuration(), transport));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public async Task RetainedWorkDayCredentialCannotStartAnExchangeInDemoMode()
    {
        var factory = new DeniedScopeFactory();
        using var worker = new WorkDayApiExchangeWorker(factory, Configuration(), NullLogger<WorkDayApiExchangeWorker>.Instance);
        await worker.StartAsync(default); await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, factory.Attempts);
    }

    [Fact]
    public void PausingSharedProcessorRetainsQueueServicesAndInternalHostedServices()
    {
        var services = new ServiceCollection();
        services.AddCommonMessagingPostgreSqlDelivery("Host=localhost;Database=unused", "test");
        services.AddHostedService<InternalWorker>();
        Assert.Contains(services,x=>x.ServiceType==typeof(IHostedService)&&x.ImplementationType==typeof(ExternalDeliveryHostedService));
        DemoIntegrationIsolation.PauseSharedDeliveryProcessor(services);
        Assert.DoesNotContain(services,x=>x.ServiceType==typeof(IHostedService)&&x.ImplementationType==typeof(ExternalDeliveryHostedService));
        Assert.Contains(services,x=>x.ServiceType==typeof(Common.Messaging.IExternalDeliveryQueue));
        Assert.Contains(services,x=>x.ServiceType==typeof(IHostedService)&&x.ImplementationType==typeof(InternalWorker));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)); }
    }
    private sealed class DeniedScopeFactory : IServiceScopeFactory
    {
        public int Attempts;
        public IServiceScope CreateScope() { Attempts++; throw new InvalidOperationException("No exchange scope may be created."); }
    }
    private sealed class InternalWorker : BackgroundService
    { protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask; }
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName {get;set;} = name;
        public string ApplicationName {get;set;} = "Aegis.Studio";
        public string ContentRootPath {get;set;} = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider {get;set;} = new NullFileProvider();
    }
}
