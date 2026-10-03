using Xunit;
using Aegis.Studio.Web.Authentication;
using Microsoft.Extensions.Configuration;

namespace Aegis.Studio.Web.Tests;

public sealed class MockAuthenticationPolicyTests
{
    [Fact]
    public void IsEnabled_RequiresDevelopmentEnvironment()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockAuthentication:Enabled"] = "true"
            })
            .Build();

        Assert.True(MockAuthenticationPolicy.IsEnabled(configuration, "Development"));
        Assert.False(MockAuthenticationPolicy.IsEnabled(configuration, "Production"));
        Assert.False(MockAuthenticationPolicy.IsEnabled(configuration, "Staging"));
    }

    [Fact]
    public void CreateIdentity_UsesConfiguredDevelopmentIdentity()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockAuthentication:UserName"] = "studio.admin",
                ["MockAuthentication:DisplayName"] = "Studio Administrator",
                ["MockAuthentication:Email"] = "studio.admin@localhost",
                ["MockAuthentication:Department"] = "Studio",
                ["MockAuthentication:Title"] = "Development Administrator",
                ["MockAuthentication:Groups:0"] = "Domain Admins"
            })
            .Build();

        MockAuthenticationIdentity identity = MockAuthenticationPolicy.CreateIdentity(
            configuration,
            "ignored-user",
            ["Administrators"]);

        Assert.Equal("studio.admin", identity.UserName);
        Assert.Equal("Studio Administrator", identity.DisplayName);
        Assert.Equal("studio.admin@localhost", identity.Email);
        Assert.Equal("Studio", identity.Department);
        Assert.Equal("Development Administrator", identity.Title);
        Assert.Contains("Domain Admins", identity.GroupNames);
    }

    [Fact]
    public void CreateIdentity_FallsBackToAdministrativeGroups()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        MockAuthenticationIdentity identity = MockAuthenticationPolicy.CreateIdentity(
            configuration,
            "developer",
            ["Domain Admins", "Administrators"]);

        Assert.Equal("developer", identity.UserName);
        Assert.Contains("Domain Admins", identity.GroupNames);
        Assert.Contains("Administrators", identity.GroupNames);
    }
}
