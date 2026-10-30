using Common.Security.Authenticators;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MockAuthenticationPolicyTests
{
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
