using Aegis.Studio.Web.Workflow;
using Xunit;

namespace Aegis.Studio.Web.Tests;

public sealed class BookingOverrideRulesTests
{
    [Theory]
    [InlineData(true,"frontdesk","operations",true)]
    [InlineData(false,"frontdesk","operations",false)]
    [InlineData(true,"frontdesk","frontdesk",false)]
    [InlineData(true," FFP\\Manager ","ffp\\manager",false)]
    public void HoldingBothRolesDoesNotPermitSelfApproval(bool operations,string requester,string actor,bool expected)
        =>Assert.Equal(expected,WorkflowRules.CanDecideBookingOverride(operations,requester,actor));
}
