using Aegis.Studio.Web.Messaging;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class MessageAttentionRulesTests
{
    [Theory]
    [InlineData("ProviderOutcomeUncertain: lost response")]
    [InlineData("ProviderOutcomeUncertainAfterLeaseExpiry")]
    public void UncertainDeliveryRequiresSupportBeforeResending(string detail)
    {
        var guidance = StudioMessageAttentionRules.Describe("Attention", detail, "Email");
        Assert.True(guidance.RequiresSupport);
        Assert.Contains("before sending again", guidance.NextStep);
        Assert.DoesNotContain(detail, guidance.Summary + guidance.NextStep);
    }

    [Theory]
    [InlineData("Email", "ProviderRejected:403")]
    [InlineData("Routing", "External sending requires a tenant-verified professional sender")]
    public void HeldDeliveriesDoNotAskStaffToDiagnoseTheTransport(string transport, string detail)
    {
        var guidance = StudioMessageAttentionRules.Describe("Attention", detail, transport);
        Assert.True(guidance.RequiresSupport);
        Assert.DoesNotContain(detail, guidance.Summary + guidance.NextStep);
    }

    [Fact]
    public void DefinitiveFailureDirectsStaffToReviewTheRecipientAndDelivery()
    {
        var guidance = StudioMessageAttentionRules.Describe("Failed", "HttpRequestException", "Email");
        Assert.False(guidance.RequiresSupport);
        Assert.Contains("recipient", guidance.NextStep);
        Assert.DoesNotContain("HttpRequestException", guidance.Summary + guidance.NextStep);
    }
}
