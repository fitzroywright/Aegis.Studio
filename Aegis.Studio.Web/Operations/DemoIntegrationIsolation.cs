using Common.Security.Authenticators;
using Common.Security.Integration;

namespace Aegis.Studio.Web.Operations;

/// <summary>
/// Optional emergency stop for every external integration. Demo data and mock authentication
/// are independent concerns and do not enable this switch.
/// </summary>
public static class ExternalIntegrationIsolation
{
    public const string ConfigurationKey = "ExternalIntegrations:IsolationEnabled";

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue(ConfigurationKey, false);

    public static bool Configure(ConfigurationManager configuration, IHostEnvironment environment)
    {
        var mock = MockAuthenticationConfiguration.Read(configuration.GetSection("MockAuthentication"), environment.EnvironmentName, "Aegis.Studio");
        if (mock.Enabled && !MockEnvironmentPolicy.IsPermitted(mock.Enabled, mock.EnvironmentName, mock.IsolationConfirmed))
            throw new InvalidOperationException("Mock authentication requires explicitly confirmed Development or Demo configuration.");

        if (!Enabled(configuration)) return false;

        // This is an explicit global emergency stop. Protected tokens, account bindings and
        // storage paths remain intact, while external workers and request-driven clients stop.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthenticationPlugin:Enabled"] = "false",
            ["Authentication:Entra:Enabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:Enabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:InboundEnabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:Delegated:Enabled"] = "false",
            ["Messaging:WhatsApp:Enabled"] = "false",
            ["Messaging:Slack:Enabled"] = "false",
            ["Messaging:Teams:Enabled"] = "false",
            ["PublicSite:PollingEnabled"] = "false",
            ["PublicSite:GalleryPushEnabled"] = "false",
            ["PublicSite:SelectionPollingEnabled"] = "false",
            ["PublicSite:BookingCatalogPublishingEnabled"] = "false",
            ["Accounting:Export:Enabled"] = "false"
        });
        return true;
    }

    public static void RequireExternalIntegrations(IConfiguration configuration)
    {
        if (Enabled(configuration)) throw new InvalidOperationException("External integrations are paused by the emergency isolation switch.");
    }

    public static void PauseSharedDeliveryProcessor(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(Common.Messaging.Hosting.ExternalDeliveryHostedService)).ToArray())
            services.Remove(descriptor);
    }
}

/// <summary>Protects request-driven typed and named clients while emergency isolation is enabled.</summary>
public sealed class ExternalIntegrationIsolationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("External integrations are paused by the emergency isolation switch.");
}
