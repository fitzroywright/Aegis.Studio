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

    private static ConfigurationManager Configuration(bool isolation = false, bool confirmed = true, bool mockEnabled = true)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ExternalIntegrationIsolation.ConfigurationKey] = isolation.ToString(),
            ["MockAuthentication:Enabled"] = mockEnabled.ToString(), ["MockAuthentication:IsolationConfirmed"] = confirmed.ToString(),
            ["MockAuthentication:Identities:0:LoginName"] = "listed", ["MockAuthentication:Identities:0:SubjectId"] = "stable",
            ["Messaging:MicrosoftGraph:Email:Enabled"] = "true", ["Messaging:MicrosoftGraph:Email:InboundEnabled"] = "true",
            ["Messaging:MicrosoftGraph:Email:Delegated:Enabled"] = "true", ["Messaging:Slack:Enabled"] = "true",
            ["PublicSite:PollingEnabled"] = "true", ["PublicSite:GalleryPushEnabled"] = "true",
            ["PublicSite:SelectionPollingEnabled"] = "true", ["PublicSite:BookingCatalogPublishingEnabled"] = "true",
            ["PublicSite:BaseUrl"] = "https://api.example.test/", ["PublicSite:IntegrationToken"] = new string('x', 40),
            ["WorkDay:ApiSyncKey"] = "synthetic-retained-credential", ["Storage:Provider"] = "S3", ["Storage:S3:RootFolder"] = "unchanged-demo-root"
        });
        return config;
    }

    [Fact]
    public void DemoModeAndLegacyDemoIsolationSettingDoNotDisableConfiguredIntegrations()
    {
        var configuration = Configuration();
        configuration["Demo:IsolateExternalIntegrations"] = "true";

        Assert.False(ExternalIntegrationIsolation.Configure(configuration, new Environment("Demo")));
        Assert.True(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Enabled"));
        Assert.True(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:InboundEnabled"));
        Assert.True(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Delegated:Enabled"));
        Assert.True(configuration.GetValue<bool>("Messaging:Slack:Enabled"));
        Assert.True(configuration.GetValue<bool>("PublicSite:PollingEnabled"));
        Assert.True(configuration.GetValue<bool>("PublicSite:GalleryPushEnabled"));
        Assert.True(configuration.GetValue<bool>("PublicSite:SelectionPollingEnabled"));
        Assert.True(configuration.GetValue<bool>("PublicSite:BookingCatalogPublishingEnabled"));
        Assert.Equal(new string('x',40), configuration["PublicSite:IntegrationToken"]);
    }

    [Fact]
    public void ExplicitEmergencyIsolationPausesDispatchWithoutErasingCredentialsOrStorage()
    {
        var configuration = Configuration(isolation:true,mockEnabled:false);

        Assert.True(ExternalIntegrationIsolation.Configure(configuration, new Environment("Production")));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Enabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:InboundEnabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Delegated:Enabled"));
        Assert.False(configuration.GetValue<bool>("Messaging:Slack:Enabled"));
        Assert.False(configuration.GetValue<bool>("PublicSite:PollingEnabled"));
        Assert.False(configuration.GetValue<bool>("PublicSite:GalleryPushEnabled"));
        Assert.False(configuration.GetValue<bool>("PublicSite:SelectionPollingEnabled"));
        Assert.False(configuration.GetValue<bool>("PublicSite:BookingCatalogPublishingEnabled"));
        Assert.Equal("synthetic-retained-credential", configuration["WorkDay:ApiSyncKey"]);
        Assert.Equal(new string('x',40), configuration["PublicSite:IntegrationToken"]);
        Assert.Equal("S3", configuration["Storage:Provider"]);
        Assert.Equal("unchanged-demo-root", configuration["Storage:S3:RootFolder"]);
    }

    [Fact]
    public void ConfirmedDemoMockAuthenticationDoesNotRequireExternalIsolation()
    {
        var configuration=Configuration();
        Assert.False(ExternalIntegrationIsolation.Configure(configuration,new Environment("Demo")));
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Demo", false)]
    public void InvalidMockAuthenticationModesStillFail(string environment,bool confirmed)
        => Assert.Throws<InvalidOperationException>(() => ExternalIntegrationIsolation.Configure(Configuration(confirmed:confirmed), new Environment(environment)));

    [Fact]
    public void NonMockProductionConfigurationIsUnchanged()
    {
        var configuration = Configuration(mockEnabled:false);
        Assert.False(ExternalIntegrationIsolation.Configure(configuration, new Environment("Production")));
        Assert.True(configuration.GetValue<bool>("Messaging:MicrosoftGraph:Email:Enabled"));
        Assert.True(configuration.GetValue<bool>("PublicSite:BookingCatalogPublishingEnabled"));
        Assert.Equal("synthetic-retained-credential", configuration["WorkDay:ApiSyncKey"]);
    }

    [Fact]
    public async Task ExplicitIsolationBlocksRequestDrivenClientsBeforeTransportRuns()
    {
        var configuration=Configuration(isolation:true,mockEnabled:false);
        var transport = new RecordingHandler();
        using var client = new HttpClient(new ExternalIntegrationIsolationHandler { InnerHandler = transport });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://external.example.test/private"));
        Assert.Equal(0, transport.Requests);
        Assert.Throws<InvalidOperationException>(() => new PublicSiteClient(configuration, transport));
        Assert.Equal(0, transport.Requests);
    }

    [Fact]
    public void DemoModeAllowsConfiguredRequestDrivenClients()
    {
        var transport=new RecordingHandler();
        using var client=new PublicSiteClient(Configuration(),transport);
        Assert.Equal(0,transport.Requests);
    }

    [Fact]
    public async Task EmergencyIsolationPreventsWorkDayExchange()
    {
        var factory = new DeniedScopeFactory();
        using var worker = new WorkDayApiExchangeWorker(factory, Configuration(isolation:true,mockEnabled:false), NullLogger<WorkDayApiExchangeWorker>.Instance);
        await worker.StartAsync(default);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, factory.Attempts);
    }

    [Fact]
    public void PausingSharedProcessorRetainsQueueServicesAndInternalHostedServices()
    {
        var services = new ServiceCollection();
        services.AddCommonMessagingPostgreSqlDelivery("Host=localhost;Database=unused", "test");
        services.AddHostedService<InternalWorker>();
        Assert.Contains(services,x=>x.ServiceType==typeof(IHostedService)&&x.ImplementationType==typeof(ExternalDeliveryHostedService));
        ExternalIntegrationIsolation.PauseSharedDeliveryProcessor(services);
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
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName {get;set;} = name;
        public string ApplicationName {get;set;} = "Aegis.Studio";
        public string ContentRootPath {get;set;} = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider {get;set;} = new NullFileProvider();
    }
}
