using Common.Diagnostics;
using Common.Security.Abstractions;
using Common.Security.Integration;
using Common.Security.Plugin.ActiveDirectory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class CommonSecurityConsumerIntegrationTests
{
    [Fact]
    public void StudioCanComposeCommonSecurityWithPlatformDiagnostics()
    {
        ServiceCollection services = new();
        services.AddCommonDiagnostics();
        services.AddCommonSecurity();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:ActiveDirectory:Domain"] = "example.local",
                ["Authentication:ActiveDirectory:SearchBase"] = "DC=example,DC=local",
                ["Authentication:ActiveDirectory:Servers:0"] = "dc1.example.local",
                ["Authentication:ActiveDirectory:Port"] = "636"
            })
            .Build();

        new ActiveDirectoryAuthenticationPlugin()
            .Register(services, configuration.GetSection("Authentication:ActiveDirectory"));
        services.AddCommonSecurityDiagnostics();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IAuthenticator>());
        Assert.Contains(provider.GetServices<IDiagnosticCheck>(), check => check.Name == "Common.Security");
    }
}
