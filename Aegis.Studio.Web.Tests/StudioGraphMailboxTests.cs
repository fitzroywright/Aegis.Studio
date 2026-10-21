using Aegis.Studio.Web.Email;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class StudioGraphMailboxTests
{
    [Fact]
    public void TransportUsesConfiguredHubInsteadOfWorkspaceIdentity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Messaging:MicrosoftGraph:Email:SenderUpn"] = " hub@studio.test ",
            ["Messaging:MicrosoftGraph:SenderUpn"] = "weddings@studio.test"
        }).Build();
        Assert.Equal("hub@studio.test", StudioGraphMailbox.Resolve(configuration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingHubFailsInsteadOfFallingBackToSendingIdentity(string? hub)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["Messaging:MicrosoftGraph:Email:SenderUpn"] = hub,
            ["Messaging:MicrosoftGraph:SenderUpn"] = "weddings@studio.test"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => StudioGraphMailbox.Resolve(configuration));
    }
}
