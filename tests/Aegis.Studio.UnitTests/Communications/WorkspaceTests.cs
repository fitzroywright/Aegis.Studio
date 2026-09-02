using Aegis.Studio.Domain.Communications;

namespace Aegis.Studio.UnitTests.Communications;

public sealed class WorkspaceTests
{
    [Fact]
    public void PublicAddressIsNormalized()
    {
        Workspace workspace = new("Weddings", "Weddings", "WEDDINGS@BRYANS.COM");
        Assert.Equal("weddings@bryans.com", workspace.PublicEmailAddress);
        Assert.Equal("weddings", workspace.Slug);
    }
}
