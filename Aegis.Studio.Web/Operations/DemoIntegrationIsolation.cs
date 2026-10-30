using Common.Security.Authenticators;
using Common.Security.Integration;

namespace Aegis.Studio.Web.Operations;

/// <summary>Demo data/credentials may be retained without dispatching to real external systems.</summary>
public static class DemoIntegrationIsolation
{
    public const string ConfigurationKey = "Demo:IsolateExternalIntegrations";

    public static bool Enabled(IConfiguration configuration) => configuration.GetValue(ConfigurationKey, false);

    public static bool Configure(ConfigurationManager configuration, IHostEnvironment environment)
    {
        var mock = MockAuthenticationConfiguration.Read(configuration.GetSection("MockAuthentication"), environment.EnvironmentName, "Aegis.Studio");
        if (mock.Enabled && !MockEnvironmentPolicy.IsPermitted(mock.Enabled, mock.EnvironmentName, mock.IsolationConfirmed))
            throw new InvalidOperationException("Mock authentication requires explicitly isolated Development or Demo configuration.");
        bool isolated = Enabled(configuration);
        if (environment.IsEnvironment("Demo") && mock.Enabled && !isolated)
            throw new InvalidOperationException("Demo mock sign-in requires external integration isolation.");
        if (!isolated) return false;
        if (!environment.IsEnvironment("Demo") || !mock.Enabled || !mock.IsolationConfirmed)
            throw new InvalidOperationException("External integration isolation requires explicitly confirmed Demo mock configuration.");

        // Nonsecret effective switches only: protected tokens, account bindings and storage paths
        // remain in their original providers. Workers are not started, so queues are not claimed.
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthenticationPlugin:Enabled"] = "false",
            ["Authentication:Entra:Enabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:Enabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:InboundEnabled"] = "false",
            ["Messaging:MicrosoftGraph:Email:Delegated:Enabled"] = "false",
            ["Messaging:MicrosoftGraph:Teams:Enabled"] = "false",
            ["Messaging:WhatsApp:Enabled"] = "false",
            ["Messaging:Slack:Enabled"] = "false",
            ["Messaging:Teams:Enabled"] = "false",
            ["PublicSite:PollingEnabled"] = "false",
            ["Accounting:Export:Enabled"] = "false"
        });
        return true;
    }

    public static void PauseSharedDeliveryProcessor(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(x => x.ServiceType == typeof(IHostedService) &&
            x.ImplementationType == typeof(Common.Messaging.Hosting.ExternalDeliveryHostedService)).ToArray())
            services.Remove(descriptor);
    }

    public static void RequireExternalIntegrations(IConfiguration configuration)
    {
        if (Enabled(configuration)) throw new InvalidOperationException("External integrations are paused for isolated Demo operation.");
    }
}

/// <summary>Also protects request-driven typed/named clients, before their transport is invoked.</summary>
public sealed class DemoIntegrationIsolationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("External integrations are paused for isolated Demo operation.");
}
