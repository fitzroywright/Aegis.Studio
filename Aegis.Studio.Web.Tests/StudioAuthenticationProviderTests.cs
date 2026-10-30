using Aegis.Studio.Web.Authentication;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioAuthenticationProviderTests
{
    [Theory]
    [InlineData(true, true, true, "Mock")]
    [InlineData(false, true, true, "EntraId")]
    [InlineData(false, false, true, "ActiveDirectory")]
    [InlineData(false, false, false, "LocalDatabase")]
    public void ExistingConfigurationSelectsOneProvider(bool mock, bool entra, bool directory, string expected)
    {
        var configuration = Config(null, directory);
        Assert.Equal(expected, StudioAuthenticationProvider.Resolve(configuration, mock, entra));
    }

    [Theory]
    [InlineData("ActiveDirectory")]
    [InlineData("LocalDatabase")]
    [InlineData("EntraId")]
    [InlineData("Mock")]
    public void ExplicitProviderWinsWithoutCredentialFallback(string provider)
    {
        Assert.Equal(provider, StudioAuthenticationProvider.Resolve(Config(provider, true), true, true));
        Assert.True(StudioAuthenticationProvider.AcceptsSource(provider, null));
        Assert.True(StudioAuthenticationProvider.AcceptsSource(provider, provider));
        Assert.False(StudioAuthenticationProvider.AcceptsSource(provider, "Other"));
    }

    [Theory]
    [InlineData("EntraId")]
    [InlineData("Mock")]
    [InlineData("ActiveDirectory")]
    [InlineData("unknown")]
    [InlineData("")]
    public void UnavailableOrInvalidProvidersFailConfiguration(string provider) =>
        Assert.Throws<InvalidOperationException>(() => StudioAuthenticationProvider.Resolve(Config(provider, false), false, false));

    private static IConfiguration Config(string? provider, bool directory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Provider"] = provider,
            ["AuthenticationPlugin:Enabled"] = directory.ToString()
        }).Build();
}
