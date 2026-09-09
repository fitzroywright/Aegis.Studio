using Common.Diagnostics;
using Common.Security.Abstractions;
using Common.Security.Integration;
using Common.Security.Options;
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
        services.AddCommonSecurity(new ActiveDirectoryAuthenticationOptions
        {
            Domain = "example.local",
            Servers = ["dc1.example.local"],
            Port = 636
        });
        services.AddCommonSecurityDiagnostics();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IAuthenticator>());
        Assert.Contains(provider.GetServices<IDiagnosticCheck>(), check => check.Name == "Common.Security");
    }
}
