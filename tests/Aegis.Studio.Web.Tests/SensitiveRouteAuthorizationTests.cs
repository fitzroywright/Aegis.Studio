using Aegis.Studio.Web.Operations;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class SensitiveRouteAuthorizationTests
{
    [Theory]
    [InlineData("GET", null, "ViewPosTransactions")]
    [InlineData("POST", "complete", "UsePos")]
    [InlineData("POST", "void", "RefundVoidPos")]
    [InlineData("POST", "refunds", "RefundVoidPos")]
    public void PosOperationsMapToExplicitPermissions(string method, string? operation, string expected)
    {
        Assert.Equal(expected, SensitiveRouteAuthorization.RequiredPosPermission(method, operation));
    }

    [Fact]
    public void SalePathParserExtractsSaleAndOperation()
    {
        Guid id = Guid.NewGuid();
        Assert.True(SensitiveRouteAuthorization.TryParsePosSalePath($"/api/pos/sales/{id}/complete", out Guid parsed, out string? operation));
        Assert.Equal(id, parsed);
        Assert.Equal("complete", operation);
    }

    [Theory]
    [InlineData("/api/jobs/123")]
    [InlineData("/api/pos/catalog")]
    [InlineData("/api/pos/sales/not-a-guid")]
    public void NonSalePathsAreRejectedByParser(string path)
    {
        Assert.False(SensitiveRouteAuthorization.TryParsePosSalePath(path, out _, out _));
    }
}
