using Common.Security.Authenticators;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MockAuthenticationPolicyTests
{
    [Theory]
    [InlineData("Demo", true, true)]
    [InlineData("Development", true, true)]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Demo", false, false)]
    public void StudioMockIsPasswordFreeButStillRequiresIsolationAndAnAllowlistedIdentity(
        string environment, bool isolated, bool allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockAuthentication:Enabled"] = "true",
            ["MockAuthentication:IsolationConfirmed"] = isolated.ToString(),
            ["MockAuthentication:Password"] = "legacy-demo-password",
            ["MockAuthentication:Identities:0:LoginName"] = "known",
            ["MockAuthentication:Identities:0:SubjectId"] = "stable-subject"
        }).Build();
        var options = Aegis.Studio.Web.Authentication.StudioAuthenticationProvider.ReadMockOptions(
            configuration.GetSection("MockAuthentication"), environment, "Aegis.Studio");
        Assert.Equal(string.Empty, options.Password);
        Assert.Equal("stable-subject", Assert.Single(options.Identities).SubjectId);
        var authenticator = new MockAuthenticator(options);
        Assert.Equal(allowed, authenticator.Authenticate("known", "").IsAuthenticated);
        Assert.False(authenticator.Authenticate("unknown", "").IsAuthenticated);
    }

    [Fact]
    public void ServerConfigurationCannotEnableProductionMockThroughOldBypassSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MockAuthentication:Enabled"]="true", ["MockAuthentication:IsolationConfirmed"]="true",
            ["MockAuthentication:AllowOutsideDevelopment"]="true",
            ["MockAuthentication:Identities:0:LoginName"]="known", ["MockAuthentication:Identities:0:SubjectId"]="stable-subject"
        }).Build();
        var options = MockAuthenticationConfiguration.Read(configuration.GetSection("MockAuthentication"), "Production", "Aegis.Studio");
        Assert.Equal(AuthenticationFailure.Prohibited, new MockAuthenticator(options).Authenticate("known", "").Failure);
    }

    [Fact]
    public void EmptyServerIdentityConfigurationCannotImpersonateARequestedAdministrator()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["MockAuthentication:Enabled"]="true",["MockAuthentication:IsolationConfirmed"]="true"
        }).Build();
        var options = MockAuthenticationConfiguration.Read(configuration.GetSection("MockAuthentication"), "Development", "Aegis.Studio");
        Assert.False(new MockAuthenticator(options).Authenticate("administrator", "").IsAuthenticated);
    }
}
